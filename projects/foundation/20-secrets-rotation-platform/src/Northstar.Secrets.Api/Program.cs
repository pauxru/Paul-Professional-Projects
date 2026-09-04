using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Northstar.Secrets.Api;
using Northstar.Secrets.Api.Auth;
using Northstar.Secrets.Api.Endpoints;
using Northstar.Secrets.Api.Middleware;
using Northstar.Secrets.Api.Services;
using Northstar.Secrets.Application;
using Northstar.Secrets.Infrastructure;
using Northstar.Secrets.Infrastructure.ExternalStores;
using Northstar.Secrets.Infrastructure.Notifications;
using Northstar.Secrets.Infrastructure.Persistence;
using Northstar.Secrets.Infrastructure.Security;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<CryptoOptions>()
    .Bind(builder.Configuration.GetSection(CryptoOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RotationConfiguration>()
    .Bind(builder.Configuration.GetSection(RotationConfiguration.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<NotificationConfiguration>()
    .Bind(builder.Configuration.GetSection(NotificationConfiguration.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var database = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
               ?? new DatabaseOptions();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
          ?? new JwtOptions();
var crypto = builder.Configuration.GetSection(CryptoOptions.SectionName).Get<CryptoOptions>()
             ?? new CryptoOptions();
var rotation = builder.Configuration.GetSection(RotationConfiguration.SectionName)
                   .Get<RotationConfiguration>()
               ?? new RotationConfiguration();
var notifications = builder.Configuration.GetSection(NotificationConfiguration.SectionName)
                        .Get<NotificationConfiguration>()
                    ?? new NotificationConfiguration();

crypto.MasterKey = Environment.GetEnvironmentVariable("NORTHSTAR_MASTER_KEY") ?? crypto.MasterKey;
if (builder.Environment.IsProduction() &&
    (jwt.SigningKey == JwtOptions.DevelopmentDefault ||
     crypto.MasterKey == LocalMasterKeyProviderOptions.DevelopmentDefault ||
     crypto.MasterKey.StartsWith("demo-only-not-a-real-secret", StringComparison.Ordinal)))
{
    throw new InvalidOperationException(
        "Production refuses to start with the clearly-fake development JWT or master key.");
}

if (!string.Equals(database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("This build supports the SQLite provider.");
}

builder.Logging.ClearProviders();
builder.Services.AddSingleton<ISecretRedactionRegistry, SecretRedactionRegistry>();
builder.Services.AddSingleton<IRedactedLogSink, ConsoleRedactedLogSink>();
builder.Services.AddSingleton<ILoggerProvider, RedactingLoggerProvider>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<SecretsDbContext>(options =>
    options.UseSqlite(database.ConnectionString));
builder.Services.AddScoped<ISecretsRepository, EfSecretsRepository>();

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IKeyProvider>(_ => new LocalMasterKeyProvider(
    new LocalMasterKeyProviderOptions
    {
        KeyVersion = crypto.KeyVersion,
        MasterKey = crypto.MasterKey
    }));
builder.Services.AddSingleton<ISecretCipher, EnvelopeEncryptionService>();
builder.Services.AddSingleton<IPlatformMetrics, PlatformMetrics>();
builder.Services.AddSingleton<ISecretGenerator, PasswordSecretGenerator>();
builder.Services.AddSingleton<ISecretGenerator, ApiKeySecretGenerator>();
builder.Services.AddSingleton<ISecretGenerator, EncryptionKeySecretGenerator>();
builder.Services.AddSingleton<ISecretGenerator, ConnectionStringSecretGenerator>();
builder.Services.AddSingleton<ISecretGenerator, KeyPairSecretGenerator>();
builder.Services.AddSingleton<ISecretGenerator, CertificateSecretGenerator>();
builder.Services.AddSingleton<ISecretGeneratorRegistry, SecretGeneratorRegistry>();
builder.Services.AddSingleton<IRotationStrategy, DualWriteRotationStrategy>();
builder.Services.AddSingleton<IRotationStrategy, SingleCutoverRotationStrategy>();
builder.Services.AddSingleton<SimulatedSecretVerifier>();
builder.Services.AddSingleton<ISecretVerifier>(provider =>
    provider.GetRequiredService<SimulatedSecretVerifier>());
builder.Services.AddSingleton<ISimulatedVerificationControl>(provider =>
    provider.GetRequiredService<SimulatedSecretVerifier>());

builder.Services.AddSingleton<IWebhookTransport, SimulatedWebhookTransport>();
builder.Services.AddSingleton<IDeadLetterStore, InMemoryDeadLetterStore>();
builder.Services.AddSingleton<IInAppInbox, InMemoryInAppInbox>();
builder.Services.AddSingleton<IEmailOutbox, InMemoryEmailOutbox>();
builder.Services.AddSingleton(new WebhookNotificationOptions
{
    SigningKey = notifications.WebhookSigningKey,
    MaxAttempts = notifications.MaxAttempts
});
builder.Services.AddSingleton<INotificationChannel, WebhookNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel, InAppNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel, EmailSimulatorNotificationChannel>();
builder.Services.AddSingleton<IAzureKeyVaultClient, UnconfiguredAzureKeyVaultClient>();
builder.Services.AddSingleton<IExternalSecretStore, AzureKeyVaultExternalSecretStore>();

builder.Services.AddSingleton(new RotationEngineOptions
{
    AcknowledgementTimeout = TimeSpan.FromMinutes(rotation.AcknowledgementTimeoutMinutes)
});
builder.Services.AddSingleton<PathPolicyEvaluator>();
builder.Services.AddScoped<PathAuthorizationService>();
builder.Services.AddSingleton<RotationScheduleCalculator>();
builder.Services.AddScoped<SecretLifecycleService>();
builder.Services.AddScoped<RotationEngine>();
builder.Services.AddScoped<AutomaticRotationScheduler>();
builder.Services.AddScoped<FourEyesService>();
builder.Services.AddScoped<BreakGlassService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddHostedService<RotationSchedulerBackgroundService>();

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<ITokenIssuer, LocalTokenIssuer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(10),
            NameClaimType = "sub"
        };
    });
builder.Services.AddAuthorization(options =>
{
    AddScopePolicy(options, ScopePolicies.ManageSecrets, "secrets.manage");
    AddScopePolicy(options, ScopePolicies.ReadValues, "secrets.read");
    AddScopePolicy(options, ScopePolicies.OperateRotations, "secrets.rotate");
    AddScopePolicy(options, ScopePolicies.ConsumerAcknowledge, "secrets.ack");
    AddScopePolicy(options, ScopePolicies.BreakGlass, "secrets.breakglass");
    AddScopePolicy(options, ScopePolicies.ApproveDestructive, "secrets.approve");
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("value-reads", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Northstar.Secrets.Api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("Northstar.Secrets.Rotation")
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(PlatformMetrics.MeterName)
        .AddConsoleExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi();
app.MapGet("/docs", () => Results.Content(
    """
    <!doctype html><html><body><h1>Northstar Secrets API</h1>
    <p>OpenAPI document: <a href="/openapi/v1.json">/openapi/v1.json</a></p>
    <p>Dashboard: <a href="/">/</a></p></body></html>
    """, "text/html"));

app.MapAuthenticationEndpoints();
app.MapSecretEndpoints();
app.MapRotationEndpoints();
app.MapConsumerEndpoints();
app.MapPolicyAndReportEndpoints();
app.MapBreakGlassEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<SecretsDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(
            CancellationToken.None);
    }
}

app.Run();

static void AddScopePolicy(
    Microsoft.AspNetCore.Authorization.AuthorizationOptions options,
    string policyName,
    string scope) =>
    options.AddPolicy(policyName, policy =>
        policy.RequireAuthenticatedUser()
            .RequireAssertion(context => ScopePolicies.HasScope(context.User, scope)));

public partial class Program;
