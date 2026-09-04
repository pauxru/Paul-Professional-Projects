using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SubscriptionBilling.Api;
using SubscriptionBilling.Application;
using SubscriptionBilling.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BillingExceptionHandler>();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase),
        "Only the SQLite provider is available in this reference implementation.")
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BillingOptions>()
    .Bind(builder.Configuration.GetSection(BillingOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.OutboundWebhookEndpoints.All(
        endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out _)),
        "Outbound webhook endpoints must be absolute URIs.")
    .ValidateOnStart();
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName))
    .ValidateOnStart();

var database = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();
var jwt = builder.Configuration
    .GetSection(JwtOptions.SectionName)
    .Get<JwtOptions>() ?? new JwtOptions();
var billing = builder.Configuration
    .GetSection(BillingOptions.SectionName)
    .Get<BillingOptions>() ?? new BillingOptions();
if (!IsPositiveAscending(billing.DunningRetryDays))
{
    throw new InvalidOperationException(
        $"Dunning retry days must be positive and ascending; received [{string.Join(',', billing.DunningRetryDays)}].");
}

if (builder.Environment.IsProduction() &&
    (jwt.SigningKey == JwtOptions.DefaultSigningKey ||
     billing.WebhookSigningSecret.StartsWith("dev-only-", StringComparison.Ordinal)))
{
    throw new InvalidOperationException(
        "Production startup refused: replace the development-only JWT and webhook signing keys.");
}

builder.Services.AddDbContext<BillingDbContext>(
    options => options.UseSqlite(database.ConnectionString));
builder.Services.AddSingleton(new BillingRuntimeOptions
{
    ClosedPeriodUsageBehavior = billing.ClosedPeriodUsageBehavior,
    TaxRoundingLevel = billing.TaxRoundingLevel,
    UsTaxRate = billing.UsTaxRate,
    DunningRetryDays = billing.DunningRetryDays,
    OutboundWebhookEndpoints = billing.OutboundWebhookEndpoints,
    WebhookSigningSecret = billing.WebhookSigningSecret
});
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIdGenerator, GuidGenerator>();
builder.Services.AddSingleton<IPaymentProvider, PaymentSimulator>();
builder.Services.AddSingleton<ITaxProvider>(_ => new LocalTaxProvider(billing.UsTaxRate));
builder.Services.AddSingleton<IOutboundWebhookTransport, SimulatedOutboundWebhookTransport>();
builder.Services.AddSingleton<IDunningNotificationHook, LoggingDunningNotificationHook>();
builder.Services.AddScoped<IWebhookReplayStore, DatabaseWebhookReplayStore>();
builder.Services.AddScoped<InvoiceGenerator>();
builder.Services.AddScoped<DunningProcessor>();
builder.Services.AddScoped<OutboundWebhookProcessor>();
builder.Services.AddScoped<BillingEngine>();
builder.Services.AddScoped<IBillingEngine>(
    services => services.GetRequiredService<BillingEngine>());
builder.Services.AddScoped<IPaymentWebhookProcessor>(
    services => services.GetRequiredService<BillingEngine>());
builder.Services.AddScoped<WebhookSignatureService>(services =>
    new WebhookSignatureService(
        services.GetRequiredService<IClock>(),
        services.GetRequiredService<IWebhookReplayStore>(),
        billing.WebhookSigningSecret,
        TimeSpan.FromMinutes(5)));
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
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub"
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("billing.read", policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            HasAnyScope(context.User, "billing.read", "billing.write", "billing.admin")));
    options.AddPolicy("billing.write", policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            HasAnyScope(context.User, "billing.write", "billing.admin")));
    options.AddPolicy("billing.admin", policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            HasAnyScope(context.User, "billing.admin")));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        var seconds = 60L;
        if (context.Lease.TryGetMetadata(
                System.Threading.RateLimiting.MetadataName.RetryAfter,
                out var retryAfter))
        {
            seconds = Math.Max(
                1,
                (retryAfter.Ticks + TimeSpan.TicksPerSecond - 1) /
                TimeSpan.TicksPerSecond);
        }

        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString();
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ??
            context.Connection.RemoteIpAddress?.ToString() ??
            "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var configured = builder.Configuration
            .GetSection(CorsOptions.SectionName)
            .Get<CorsOptions>() ?? new CorsOptions();
        policy.WithOrigins(configured.AllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Subscription Billing & Usage Metering Engine",
        Version = "v1",
        Description = "Self-directed engineering case study API."
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header
    });
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("subscription-billing-engine"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(BillingTelemetry.SourceName)
            .AddAspNetCoreInstrumentation();
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(BillingTelemetry.SourceName)
            .AddAspNetCoreInstrumentation();
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            metrics.AddConsoleExporter();
        }
    });

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<InvoiceRunBackgroundService>();
    builder.Services.AddHostedService<DunningBackgroundService>();
    builder.Services.AddHostedService<OutboundWebhookBackgroundService>();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment())
    {
        await SeedData.InitializeAsync(
            db,
            scope.ServiceProvider.GetRequiredService<IClock>(),
            CancellationToken.None);
    }
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new()
{
    Predicate = registration => registration.Tags.Contains("ready")
});
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Billing Engine v1");
        options.RoutePrefix = "docs";
    });
}

app.MapCatalogueEndpoints();
app.MapBillingEndpoints();

app.Run();

static bool HasAnyScope(System.Security.Claims.ClaimsPrincipal principal, params string[] required)
{
    var scopes = principal.FindAll("scope")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToHashSet(StringComparer.Ordinal);
    return required.Any(scopes.Contains);
}

static bool IsPositiveAscending(IReadOnlyList<int> values)
{
    if (values.Count == 0 || values[0] <= 0)
    {
        return false;
    }

    for (var index = 1; index < values.Count; index++)
    {
        if (values[index] <= values[index - 1])
        {
            return false;
        }
    }

    return true;
}

public partial class Program;
