using System.Text;
using System.Threading.RateLimiting;
using FraudPipeline.Api.Contracts;
using FraudPipeline.Api.Endpoints;
using FraudPipeline.Api.Middleware;
using FraudPipeline.Api.Options;
using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.Cases;
using FraudPipeline.Application.Feedback;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Ingestion;
using FraudPipeline.Application.Rules;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Application.Shadow;
using FraudPipeline.Application.Synthetic;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;
using FraudPipeline.Infrastructure.Auth;
using FraudPipeline.Infrastructure.Persistence;
using FraudPipeline.Infrastructure.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------- Configuration ----------
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (builder.Environment.IsProduction() && jwtOptions.SigningKey.StartsWith("dev-only", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Refusing to boot in Production with the default JWT signing key.");
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<ITokenIssuer, DevTokenIssuer>();

var ingestionOptions = builder.Configuration.GetSection("Ingestion").Get<IngestionOptions>() ?? new IngestionOptions();
builder.Services.AddSingleton(ingestionOptions);
var scoringOptions = builder.Configuration.GetSection("Scoring").Get<ScoringOptions>() ?? new ScoringOptions();
builder.Services.AddSingleton(scoringOptions);
var caseOptions = builder.Configuration.GetSection("CaseManagement").Get<CaseManagementOptions>() ?? new CaseManagementOptions();
builder.Services.AddSingleton(caseOptions);

// ---------- Persistence ----------
var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
builder.Services.AddDbContext<FraudDbContext>(o => o.UseSqlite(dbOptions.ConnectionString));

builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<IScoringDecisionRepository, ScoringDecisionRepository>();
builder.Services.AddScoped<IRulesetRepository, RulesetRepository>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<ICaseRepository, CaseRepository>();
builder.Services.AddScoped<IListEntryRepository, ListEntryRepository>();
builder.Services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();

// ---------- Core services ----------
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
builder.Services.AddSingleton<FeatureStoreRuntime>();
builder.Services.AddSingleton<FeatureStoreService>();
builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddSingleton<ScoringMetrics>();
builder.Services.AddScoped<ScoringService>();
builder.Services.AddScoped<CaseManagementService>();
builder.Services.AddScoped<DetectionEvaluator>();
builder.Services.AddScoped<ShadowComparator>();
builder.Services.AddSingleton<PartitionedTransactionBus>();

// ---------- Auth ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("risk:score",       p => p.RequireAuthenticatedUser().RequireClaim("scope", "risk:score"))
    .AddPolicy("risk:investigate", p => p.RequireAuthenticatedUser().RequireClaim("scope", "risk:investigate"))
    .AddPolicy("risk:approve",     p => p.RequireAuthenticatedUser().RequireClaim("scope", "risk:approve"))
    .AddPolicy("risk:admin",       p => p.RequireAuthenticatedUser().RequireClaim("scope", "risk:admin"));

// ---------- Cross-cutting ----------
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<FraudDbContext>("db");

var rlOptions = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx => RateLimitPartition.GetTokenBucketLimiter(
        partitionKey: ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        factory: _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = rlOptions.BurstSize,
            TokensPerPeriod = Math.Max(1, rlOptions.RequestsPerMinute / 60),
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0
        }));
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("FraudPipeline"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource("FraudPipeline.Scoring").AddConsoleExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter(ScoringMetrics.MeterName).AddConsoleExporter());

// ---------- Build ----------
var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapAuthEndpoints();
app.MapTransactionEndpoints();
app.MapRulesetEndpoints();
app.MapFeatureEndpoints();
app.MapAlertsAndCasesEndpoints();
app.MapMetricsEndpoints();

// Seed on startup unless Testing.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    await Startup.InitialiseAsync(scope.ServiceProvider);
}

app.Run();

public static class Startup
{
    public static async Task InitialiseAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<FraudDbContext>();
        await db.Database.EnsureCreatedAsync();
        var rulesets = services.GetRequiredService<IRulesetRepository>();
        var ids = services.GetRequiredService<IIdGenerator>();
        var clock = services.GetRequiredService<IClock>();

        if (await rulesets.GetActiveAsync() is null)
        {
            var v1 = DefaultRulesets.BuildV1();
            var r = new Ruleset(ids.NewGuid(), v1.Version, v1.Name, RulesetSerializer.Serialize(v1), clock.UtcNow);
            r.Activate(clock.UtcNow);
            await rulesets.AddAsync(r);

            var challenger = DefaultRulesets.BuildV1Challenger();
            var rc = new Ruleset(ids.NewGuid(), challenger.Version, challenger.Name, RulesetSerializer.Serialize(challenger), clock.UtcNow);
            rc.MakeShadow();
            await rulesets.AddAsync(rc);
            await rulesets.SaveAsync();
        }
    }
}

public partial class Program;
