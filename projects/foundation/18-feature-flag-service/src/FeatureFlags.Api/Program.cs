using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FeatureFlags.Api.Contracts;
using FeatureFlags.Api.Endpoints;
using FeatureFlags.Api.Security;
using FeatureFlags.Api.Ui;
using FeatureFlags.Application;
using FeatureFlags.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOptions<DatabaseOptions>().Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
var database = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (!string.Equals(database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("This reference implementation defaults to the SQLite adapter.");
}
if (builder.Environment.IsProduction() && jwt.SigningKey.StartsWith("dev-only-not-a-real-secret", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Production refuses the development-only JWT signing key.");
}

builder.Services.AddFeatureFlagInfrastructure(database.ConnectionString);
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateLifetime = true, NameClaimType = ClaimTypes.NameIdentifier, ClockSkew = TimeSpan.FromSeconds(15)
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FlagsRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasScope("flags:read") || context.User.HasScope("flags:write") || context.User.HasScope("flags:approve")));
    options.AddPolicy("FlagsWrite", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasScope("flags:write")));
    options.AddPolicy("FlagsApprove", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasScope("flags:approve")));
});
builder.Services.AddCors(options => options.AddPolicy("admin", policy => policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5018"]).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("sdk", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers["X-Sdk-Key"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddConsoleExporter());

var app = builder.Build();
await app.Services.EnsureDatabaseAndSeedAsync(app.Environment.IsDevelopment());

app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception switch
    {
        NotFoundException => StatusCodes.Status404NotFound,
        UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
        ApprovalRequiredException or FourEyesViolationException or DomainValidationException => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError
    };
    await Results.Problem(statusCode: status, title: exception?.GetType().Name ?? "Unhandled error", detail: exception?.Message, extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId)) correlationId = Guid.NewGuid().ToString("N");
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
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});
app.UseCors("admin");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi();
app.MapGet("/docs", () => Results.Content("<html><body><h1>Feature Flags API</h1><p><a href='/openapi/v1.json'>OpenAPI document</a></p></body></html>", "text/html"));
app.MapGet("/", () => Results.Content(AdminUi.Html, "text/html"));
app.MapPost("/api/v1/auth/token", (TokenRequest request, ITokenIssuer issuer, IWebHostEnvironment environment) =>
{
    if (environment.IsProduction()) return Results.NotFound();
    var actor = string.IsNullOrWhiteSpace(request.Actor) ? "local-admin" : request.Actor;
    var scopes = request.Scopes?.Count > 0 ? request.Scopes : ["flags:read", "flags:write", "flags:approve"];
    return Results.Ok(new { accessToken = issuer.Issue(actor, scopes), tokenType = "Bearer", expiresIn = 14_400 });
});
app.MapProjectEndpoints();
app.MapSdkEndpoints();
app.MapAnalyticsEndpoints();
app.Run();

public sealed class TokenRequest
{
    public string? Actor { get; init; }
    public IReadOnlyList<string>? Scopes { get; init; }
}

public partial class Program;
