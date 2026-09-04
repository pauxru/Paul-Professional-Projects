using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FieldOps.Api;
using FieldOps.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["correlationId"] =
            context.HttpContext.Response.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? context.HttpContext.TraceIdentifier;
    };
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi("v1");
builder.Services.AddHttpContextAccessor();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TenantResolutionOptions>()
    .Bind(builder.Configuration.GetSection(TenantResolutionOptions.SectionName))
    .Validate(options => options.Precedence.Length > 0, "At least one tenant strategy is required.")
    .ValidateOnStart();
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName))
    .ValidateOnStart();

var database = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new();
var tenantResolution = builder.Configuration.GetSection(TenantResolutionOptions.SectionName).Get<TenantResolutionOptions>() ?? new();
var cors = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new();
var objectStore = builder.Configuration.GetSection(LocalObjectStoreOptions.SectionName).Get<LocalObjectStoreOptions>() ?? new();
var billing = builder.Configuration.GetSection(BillingSimulatorOptions.SectionName).Get<BillingSimulatorOptions>() ?? new();

if (!string.Equals(database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("This build supports the SQLite adapter. Configure Database:Provider=Sqlite.");
}
if (builder.Environment.IsProduction() && jwt.SigningKey.StartsWith("dev-only-", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Production refuses to start with the development JWT signing key.");
}

builder.Services.AddFieldOpsInfrastructure(database.ConnectionString, objectStore, billing);
builder.Services.AddFieldOpsSecurity(jwt);
builder.Services.AddSingleton(tenantResolution);
builder.Services.AddSingleton(cors);
builder.Services.AddSingleton<ITenantResolutionStrategy, JwtTenantResolutionStrategy>();
builder.Services.AddSingleton<ITenantResolutionStrategy, HeaderTenantResolutionStrategy>();
builder.Services.AddSingleton<ITenantResolutionStrategy, SubdomainTenantResolutionStrategy>();
builder.Services.AddSingleton<TenantRequestMeter>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(cors.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var tenant = context.Request.Headers[tenantResolution.HeaderName].FirstOrDefault()
            ?? context.Request.Host.Host
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            tenant,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5_000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(
            statusCode: 429,
            title: "Tenant request rate exceeded",
            type: "https://fieldops.local/problems/rate-limit",
            extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier })
            .ExecuteAsync(context.HttpContext);
    };
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("FieldOps.Api"))
    .WithTracing(tracing => tracing
        .AddSource(FieldOpsTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(FieldOpsTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId)) correlationId = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next(context);
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'";
    if (context.Request.IsHttps) context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next(context);
});
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<TenantPlanRateLimitMiddleware>();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi("/openapi/{documentName}.json");
app.MapGet("/docs", () => Results.Content(
    """
    <!doctype html><html><head><title>FieldOps API</title></head>
    <body><h1>FieldOps API</h1><p><a href="/openapi/v1.json">OpenAPI v1 document</a></p></body></html>
    """, "text/html")).ExcludeFromDescription();

app.MapAuthEndpoints();
app.MapFieldOperationsEndpoints();
app.MapCommercialEndpoints();
app.MapAdminEndpoints();

if (app.Environment.IsDevelopment())
{
    await DemoData.SeedAsync(app.Services);
}

app.Run();

public partial class Program;
