using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using EnterpriseSearch.Api;
using EnterpriseSearch.Api.Endpoints;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Application.Security;
using EnterpriseSearch.Infrastructure.Persistence;
using EnterpriseSearch.Infrastructure.Search;
using EnterpriseSearch.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<SqliteReadinessHealthCheck>("sqlite", tags: ["ready"]);
builder.Services.AddOptions<DatabaseOptions>().Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<QueryLimits>().Bind(builder.Configuration.GetSection(QueryLimits.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<Bm25Options>().Bind(builder.Configuration.GetSection(Bm25Options.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<FunctionScoreOptions>().Bind(builder.Configuration.GetSection(FunctionScoreOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TelemetryOptions>().Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName)).ValidateOnStart();
builder.Services.AddOptions<CorsOptions>().Bind(builder.Configuration.GetSection(CorsOptions.SectionName)).ValidateOnStart();

builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<QueryLimits>>().Value);
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<FunctionScoreOptions>>().Value);
var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var telemetryOptions = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();
if (!string.Equals(databaseOptions.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This reference implementation supports Sqlite as its local persistence provider.");
if (builder.Environment.IsProduction() && jwtOptions.SigningKey == JwtOptions.DefaultDevelopmentSigningKey) throw new InvalidOperationException("Production refuses the default development JWT signing key.");

builder.Services.AddDbContext<SearchDbContext>(options => options.UseSqlite(databaseOptions.ConnectionString));
builder.Services.AddSingleton<ITextAnalyzer, TextAnalyzer>();
builder.Services.AddSingleton<IEmbeddingModel>(provider => new DeterministicEmbeddingModel(provider.GetRequiredService<ITextAnalyzer>()));
builder.Services.AddSingleton<SearchCluster>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ISearchAnalytics, InMemorySearchAnalytics>();
builder.Services.AddSingleton<QueryStringParser>();
builder.Services.AddSingleton<IScorer>(provider => new Bm25Scorer(provider.GetRequiredService<IOptions<Bm25Options>>().Value));
builder.Services.AddSingleton<FunctionScorer>();
builder.Services.AddSingleton<SearchEngine>();
builder.Services.AddSingleton<RelevanceEvaluationHarness>();
builder.Services.AddSingleton<SearchBenchmark>();
builder.Services.AddScoped<IIndexStateStore, SqliteIndexStateStore>();
builder.Services.AddScoped<SearchIndexService>();
builder.Services.AddScoped<ContosoDemoSeeder>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddHostedService<IndexRefreshWorker>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(15)
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SearchRead", policy => policy.RequireAuthenticatedUser().RequireClaim("scope", "search.read", "search.manage"));
    options.AddPolicy("SearchManage", policy => policy.RequireAuthenticatedUser().RequireClaim("scope", "search.manage"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 240,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { title = "Rate limit exceeded", status = 429 }, cancellationToken);
    };
});
builder.Services.AddCors(options => options.AddPolicy("ApiCors", policy =>
{
    if (corsOptions.AllowedOrigins.Length > 0)
    {
        policy.WithOrigins(corsOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
    }
}));
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);

var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (telemetryOptions.ConsoleExporter) tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddMeter(SearchTelemetry.MeterName);
        if (telemetryOptions.ConsoleExporter) metrics.AddConsoleExporter();
    });

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128) correlationId = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});
app.UseCors("ApiCors");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") }).AllowAnonymous();
app.MapOpenApi().AllowAnonymous();
app.MapGet("/docs", () => Results.Redirect("/openapi/v1.json")).AllowAnonymous();
app.MapAuthEndpoints();
app.MapIndexEndpoints();
app.MapSearchEndpoints();
app.MapAnalyticsEndpoints();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
    await database.Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<SearchIndexService>().RestoreAsync();
    if (app.Environment.IsDevelopment() || args.Contains("--evaluate", StringComparer.OrdinalIgnoreCase))
    {
        await scope.ServiceProvider.GetRequiredService<ContosoDemoSeeder>().SeedAsync();
    }
}

if (args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var report = scope.ServiceProvider.GetRequiredService<SearchBenchmark>().Run();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Contains("--evaluate", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var report = scope.ServiceProvider.GetRequiredService<RelevanceEvaluationHarness>().Run("catalogue", GoldenEvaluationSet.Create());
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

app.Run();

public partial class Program;
