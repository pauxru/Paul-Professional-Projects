using System.Text;
using System.Text.Json.Serialization;
using IntegrationHub.Api;
using IntegrationHub.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("sqlite");
builder.Services.AddIntegrationHubInfrastructure(builder.Configuration);
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ApiSecurityOptions>()
    .Bind(builder.Configuration.GetSection(ApiSecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var configuredSecrets = builder.Configuration.GetSection(SecretsOptions.SectionName).Get<SecretsOptions>()
                        ?? new SecretsOptions();
var connectorHosts = builder.Configuration.GetSection(ConnectorHostOptions.SectionName).Get<ConnectorHostOptions>()
                     ?? new ConnectorHostOptions();
if (builder.Environment.IsProduction()
    && (jwt.SigningKey.StartsWith("dev-only", StringComparison.Ordinal)
        || configuredSecrets.MasterKey == "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        || connectorHosts.AllowPrivateNetworks))
{
    throw new InvalidOperationException(
        "Production requires non-default signing/encryption keys and private-network connector access disabled.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(15)
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Read, policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopeAuthorization.HasScope(context.User, "hub.read") || ScopeAuthorization.HasScope(context.User, "hub.admin")))
    .AddPolicy(Policies.Write, policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopeAuthorization.HasScope(context.User, "hub.write") || ScopeAuthorization.HasScope(context.User, "hub.admin")))
    .AddPolicy(Policies.Admin, policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopeAuthorization.HasScope(context.User, "hub.admin")));
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5012"];
    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://httpstatuses.com/429",
            title = "Rate limit exceeded",
            status = 429,
            traceId = context.HttpContext.TraceIdentifier
        }, cancellationToken: token);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddFixedWindowLimiter("webhooks", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("IntegrationHub.Api"))
    .WithTracing(tracing => tracing
        .AddSource(FlowRunner.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(FlowRunner.MeterName)
        .AddMeter(RestConnector.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());
builder.Services.AddHostedService<ScheduledFlowWorker>();
builder.Services.AddHostedService<HistoryRetentionWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Integration Hub v1");
    });
}

app.MapAuthEndpoints();
app.MapConnectorEndpoints();
app.MapFlowEndpoints();
app.MapRunEndpoints();
app.MapMappingEndpoints();
app.MapDeadLetterEndpoints();
app.MapSecretEndpoints();
app.MapWebhookEndpoints();

await DatabaseInitializer.InitializeAsync(app.Services, app.Environment);
app.Run();

public partial class Program;
