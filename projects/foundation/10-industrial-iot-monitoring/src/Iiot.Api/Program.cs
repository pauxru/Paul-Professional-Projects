using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Iiot.Api;
using Iiot.Application;
using Iiot.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
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

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (builder.Environment.IsProduction() && jwtOptions.SigningKey == JwtOptions.DefaultSigningKey)
{
    throw new InvalidOperationException("The development JWT signing key cannot be used in Production.");
}

builder.Services.AddIiotInfrastructure(databaseOptions);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddScoped<DeviceRegistryService>();
builder.Services.AddScoped<TelemetryIngestionService>();
builder.Services.AddScoped<AlertRuleOrchestrator>();
builder.Services.AddScoped<AlertEscalationService>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddSingleton<GatewayNetworkControl>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(JwtOptions.GetKey(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(15)
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Operator, policy => policy.RequireAuthenticatedUser().RequireAssertion(HasScope("operator", "admin")));
    options.AddPolicy(Policies.Admin, policy => policy.RequireAuthenticatedUser().RequireAssertion(HasScope("admin")));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("device", context =>
    {
        var device = context.Request.Headers["X-Device-Id"].ToString();
        return RateLimitPartition.GetFixedWindowLimiter(
            string.IsNullOrWhiteSpace(device) ? context.Connection.RemoteIpAddress?.ToString() ?? "unknown" : device,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(IiotMetrics.MeterName).AddAspNetCoreInstrumentation())
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<BrokerHostedService>();
    builder.Services.AddHostedService<DemoRuntimeHostedService>();
}

var app = builder.Build();
await app.Services.EnsureIiotDatabaseAsync();
if (app.Environment.IsDevelopment())
{
    await DemoSeed.SeedAsync(app.Services);
}

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("N");
    }

    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
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
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi("/openapi/{documentName}.json");
app.MapOpenApiDocumentation();

app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapTelemetryEndpoints();
app.MapRuleEndpoints();
app.MapAlertEndpoints();
app.MapCommandEndpoints();
app.MapFirmwareEndpoints();
app.MapDashboardEndpoints();

app.Run();

static Func<AuthorizationHandlerContext, bool> HasScope(params string[] required) =>
    context => context.User.FindAll("scope")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Any(scope => required.Contains(scope, StringComparer.OrdinalIgnoreCase));

public partial class Program;
