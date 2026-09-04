using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.RateLimiting;
using AuditPlatform.Api.Endpoints;
using AuditPlatform.Api.Middleware;
using AuditPlatform.Api.Options;
using AuditPlatform.Api.Seeding;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Exports;
using AuditPlatform.Application.Query;
using AuditPlatform.Application.Reports;
using AuditPlatform.Application.Retention;
using AuditPlatform.Application.Schemas;
using AuditPlatform.Application.Security;
using AuditPlatform.Application.Verification;
using AuditPlatform.Domain.Ids;
using AuditPlatform.Domain.Time;
using AuditPlatform.Infrastructure.Events;
using AuditPlatform.Infrastructure.Persistence;
using AuditPlatform.Infrastructure.Persistence.Interceptors;
using AuditPlatform.Infrastructure.Query;
using AuditPlatform.Infrastructure.Signing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- typed configuration bound at start-up ---
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SigningOptions>()
    .Bind(builder.Configuration.GetSection(SigningOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SeedOptions>()
    .Bind(builder.Configuration.GetSection(SeedOptions.SectionName));

// --- guard against default signing key in Production ---
if (builder.Environment.IsProduction())
{
    var key = builder.Configuration[$"{JwtOptions.SectionName}:SigningKey"];
    if (string.IsNullOrEmpty(key) || key.StartsWith("dev-only", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to boot: default dev-only Jwt signing key detected in Production.");
}

// --- expose options directly (test overrides use IOptions typically, but we also register raw) ---
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<JwtOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SigningOptions>>().Value);

// --- persistence ---
builder.Services.AddSingleton<AppendOnlyInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, opts) =>
{
    var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    opts.UseSqlite(db.ConnectionString);
});

// --- ports and adapters ---
builder.Services.AddScoped<IAuditEventStore, AuditEventStore>();
builder.Services.AddScoped<ICheckpointStore, CheckpointStore>();
builder.Services.AddScoped<ISchemaRegistry, EventSchemaRegistry>();
builder.Services.AddScoped<IRetentionStore, RetentionStore>();
builder.Services.AddScoped<ILegalHoldStore, LegalHoldStore>();
builder.Services.AddScoped<ISavedQueryStore, SavedQueryStore>();
builder.Services.AddScoped<IDeadLetterStore, DeadLetterStore>();
builder.Services.AddScoped<ISearchIndex, InvertedSearchIndex>();

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();
builder.Services.AddSingleton<ITenantLock, InMemoryTenantLock>();
builder.Services.AddSingleton<ISigningService>(sp =>
{
    var s = sp.GetRequiredService<IOptions<SigningOptions>>().Value;
    return new RsaSigningService(s.KeyId);
});
builder.Services.AddSingleton<IAuditSelfLogger, SelfLogger>();

// --- application services ---
builder.Services.AddScoped(sp => new IngestOptions());
builder.Services.AddScoped<AuditIngestService>();
builder.Services.AddScoped<VerificationService>();
builder.Services.AddScoped<RedactionService>();
builder.Services.AddScoped<QueryService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<PrivilegedAccessReportService>();
builder.Services.AddScoped<SchemaRegistryService>();

// --- auth ---
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("audit:write", p => p.RequireAuthenticatedUser().RequireClaim("scope", "audit:write"));
    o.AddPolicy("audit:read", p => p.RequireAuthenticatedUser().RequireClaim("scope", "audit:read"));
    o.AddPolicy("audit:verify", p => p.RequireAuthenticatedUser().RequireClaim("scope", "audit:verify"));
    o.AddPolicy("audit:admin", p => p.RequireAuthenticatedUser().RequireClaim("scope", "audit:admin"));
    o.AddPolicy("audit:export", p => p.RequireAuthenticatedUser().RequireClaim("scope", "audit:export"));
});

// --- observability ---
var meter = new Meter("AuditPlatform.Api");
builder.Services.AddSingleton(meter);

builder.Services.AddOpenTelemetry()
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation();
        m.AddMeter("AuditPlatform.Api");
        m.AddConsoleExporter();
    })
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        t.AddConsoleExporter();
    });

// --- other framework services ---
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2000,
            TokensPerPeriod = 500,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 100,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsync("rate limit exceeded", ct);
    };
});

var app = builder.Build();

// --- initial database create ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- middleware ordering: problem details -> correlation -> security headers -> rate limit -> authn -> authz ---
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// --- endpoints ---
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi();

app.MapAuthEndpoints(app.Environment);
app.MapEventEndpoints();
app.MapVerificationEndpoints();
app.MapSchemaEndpoints();
app.MapRetentionEndpoints();
app.MapLegalHoldEndpoints();
app.MapExportEndpoints();
app.MapReportEndpoints();

// --- seed ---
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await Seeder.SeedAsync(scope.ServiceProvider, app.Environment, default);
}

app.Run();

// Enable WebApplicationFactory<Program> from the integration test project.
public partial class Program;
