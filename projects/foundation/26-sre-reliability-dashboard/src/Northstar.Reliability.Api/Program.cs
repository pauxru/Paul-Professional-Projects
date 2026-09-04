using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Northstar.Reliability.Api.Endpoints;
using Northstar.Reliability.Api.Models;
using Northstar.Reliability.Application.Abstractions;
using Northstar.Reliability.Application.Services;
using Northstar.Reliability.Application.Simulation;
using Northstar.Reliability.Infrastructure.Services;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ErrorBudgetPolicyOptions>()
    .Bind(builder.Configuration.GetSection(ErrorBudgetPolicyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.RiskyDeployFreezeRemainingPercent < options.WarningRemainingPercent,
        "Risky deployment freeze threshold must be below warning threshold.")
    .ValidateOnStart();
builder.Services.AddOptions<MetricRetentionOptions>()
    .Bind(builder.Configuration.GetSection(MetricRetentionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BurnRateAlertOptions>()
    .Bind(builder.Configuration.GetSection(BurnRateAlertOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options =>
        options.FastPageShortWindowMinutes < options.FastPageLongWindowMinutes &&
        options.SlowPageShortWindowMinutes < options.SlowPageLongWindowMinutes &&
        options.SustainedTicketShortWindowMinutes < options.SustainedTicketLongWindowMinutes,
        "Alert confirmation windows must preserve the configured multi-window ordering.")
    .ValidateOnStart();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var policyOptions = builder.Configuration.GetSection(ErrorBudgetPolicyOptions.SectionName).Get<ErrorBudgetPolicyOptions>() ?? new ErrorBudgetPolicyOptions();
var retentionOptions = builder.Configuration.GetSection(MetricRetentionOptions.SectionName).Get<MetricRetentionOptions>() ?? new MetricRetentionOptions();
var alertOptions = builder.Configuration.GetSection(BurnRateAlertOptions.SectionName).Get<BurnRateAlertOptions>() ?? new BurnRateAlertOptions();
if (builder.Environment.IsProduction() && jwtOptions.SigningKey == JwtOptions.DevelopmentSigningKey)
{
    throw new InvalidOperationException("The development JWT signing key cannot be used in Production.");
}

builder.Services.AddReliabilityInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SyntheticTelemetrySimulator>();
builder.Services.AddSingleton(new Northstar.Reliability.Domain.Slo.ErrorBudgetPolicy(
    policyOptions.WarningRemainingPercent,
    policyOptions.RiskyDeployFreezeRemainingPercent));
builder.Services.AddSingleton(new MetricRetentionPolicy(
    TimeSpan.FromHours(retentionOptions.RawRetentionHours),
    TimeSpan.FromDays(retentionOptions.HourlyRetentionDays)));
builder.Services.AddSingleton<IReadOnlyList<Northstar.Reliability.Domain.Slo.MultiWindowAlertRule>>(
[
    new("fast-page", Northstar.Reliability.Domain.Slo.AlertSeverity.Page,
        new(TimeSpan.FromMinutes(alertOptions.FastPageLongWindowMinutes), alertOptions.FastPageLongThreshold),
        new(TimeSpan.FromMinutes(alertOptions.FastPageShortWindowMinutes), alertOptions.FastPageShortThreshold),
        2m),
    new("slow-page", Northstar.Reliability.Domain.Slo.AlertSeverity.Page,
        new(TimeSpan.FromMinutes(alertOptions.SlowPageLongWindowMinutes), alertOptions.SlowPageLongThreshold),
        new(TimeSpan.FromMinutes(alertOptions.SlowPageShortWindowMinutes), alertOptions.SlowPageShortThreshold),
        5m),
    new("sustained-ticket", Northstar.Reliability.Domain.Slo.AlertSeverity.Ticket,
        new(TimeSpan.FromMinutes(alertOptions.SustainedTicketLongWindowMinutes), alertOptions.SustainedTicketLongThreshold),
        new(TimeSpan.FromMinutes(alertOptions.SustainedTicketShortWindowMinutes), alertOptions.SustainedTicketShortThreshold),
        10m)
]);
builder.Services.AddScoped<ReliabilityFacade>();
builder.Services.AddSingleton<ITokenIssuer>(_ => new JwtTokenIssuer(jwtOptions));
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("api", limiter =>
    {
        limiter.PermitLimit = 1_000;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
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
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("reliability.read", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "reliability.read")));
    options.AddPolicy("reliability.write", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "reliability.write")));
    options.AddPolicy("reliability.admin", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "reliability.admin")));
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddSource(ReliabilityInstrumentation.SourceName).AddConsoleExporter())
    .WithMetrics(metrics => metrics.AddMeter(ReliabilityInstrumentation.SourceName).AddConsoleExporter());

var app = builder.Build();

app.UseExceptionHandler(errorApplication => errorApplication.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception switch
    {
        BadHttpRequestException => StatusCodes.Status400BadRequest,
        Northstar.Reliability.Domain.Common.DomainRuleViolationException => StatusCodes.Status422UnprocessableEntity,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status500InternalServerError
    };
    var title = status switch
    {
        StatusCodes.Status422UnprocessableEntity => "Domain rule rejected the request",
        StatusCodes.Status400BadRequest => "Request body is invalid",
        StatusCodes.Status404NotFound => "Resource was not found",
        _ => "An unexpected error occurred"
    };
    var problem = new ProblemDetails
    {
        Status = status,
        Title = title,
        Detail = status == StatusCodes.Status500InternalServerError ? "See server logs using the correlation identifier." : exception?.Message,
        Instance = context.Request.Path
    };
    problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(problem);
}));
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }

    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapGet("/docs", () => Results.Content(
        "<!doctype html><title>Northstar Reliability API</title><h1>Northstar Reliability API</h1><p><a href=\"/openapi/v1.json\">OpenAPI document</a></p>",
        "text/html")).AllowAnonymous();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAuthEndpoints();
app.MapServiceEndpoints();
app.MapSloEndpoints();
app.MapAlertEndpoints();
app.MapIncidentEndpoints();
app.MapPostmortemEndpoints();
app.MapOperationsEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

if (!app.Environment.IsEnvironment("Testing"))
{
    await app.Services.EnsureDatabaseCreatedAsync();
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ReliabilityFacade>().SeedDemoAsync();
    }
}

app.Run();

static bool HasScope(ClaimsPrincipal principal, string requiredScope) =>
    principal.Claims
        .Where(claim => claim.Type is "scope" or "scp")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(scope => string.Equals(scope, requiredScope, StringComparison.OrdinalIgnoreCase));

public partial class Program;
