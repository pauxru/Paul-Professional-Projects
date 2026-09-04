using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SavannaLogistics.Api;
using SavannaLogistics.Application;
using SavannaLogistics.Infrastructure;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["correlationId"] =
            context.HttpContext.Items["CorrelationId"]?.ToString() ?? context.HttpContext.TraceIdentifier;
    };
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<AlertOptions>()
    .Bind(builder.Configuration.GetSection(AlertOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<EtaOptions>()
    .Bind(builder.Configuration.GetSection(EtaOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (builder.Environment.IsProduction() && jwt.SigningKey == JwtOptions.DefaultSigningKey)
{
    throw new InvalidOperationException("Production cannot start with the development JWT signing key.");
}

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
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FleetRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopePolicies.HasScope(context.User, "fleet.read")));
    options.AddPolicy("FleetWrite", policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopePolicies.HasScope(context.User, "fleet.write")));
    options.AddPolicy("TelemetryIngest", policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopePolicies.HasScope(context.User, "telemetry.ingest")));
    options.AddPolicy("Operations", policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => ScopePolicies.HasScope(context.User, "operations")));
});
builder.Services.AddSingleton<TokenIssuer>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("telemetry-ingest", httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            httpContext.User.FindFirst("sub")?.Value ??
            httpContext.Connection.RemoteIpAddress?.ToString() ??
            "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                TokensPerPeriod = 20,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
});
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("dashboard", policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddSavannaInfrastructure();

var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(LogisticsTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(LogisticsTelemetry.MeterName));
if (builder.Configuration.GetValue<bool>("Observability:ConsoleExporter"))
{
    telemetry.WithTracing(tracing => tracing.AddConsoleExporter())
        .WithMetrics(metrics => metrics.AddConsoleExporter());
}

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    const string header = "X-Correlation-Id";
    var correlationId = context.Request.Headers.TryGetValue(header, out var inbound) &&
                        !string.IsNullOrWhiteSpace(inbound)
        ? inbound.ToString()
        : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlationId;
    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers[header] = correlationId;
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
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    await next();
});
app.UseCors("dashboard");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapOpenApi();
if (!app.Environment.IsProduction())
{
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("/openapi/v1.json", "Savanna Logistics API v1");
    });
    app.MapDashboard();
    app.MapAuthEndpoints();
}
app.MapVehicleEndpoints();
app.MapPlanningEndpoints();
app.MapTelemetryEndpoints();
app.MapOperationalEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
    await dbContext.Database.MigrateAsync();
    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(CancellationToken.None);
    }
}

app.Run();

public partial class Program;
