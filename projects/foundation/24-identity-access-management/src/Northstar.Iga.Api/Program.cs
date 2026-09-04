using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Northstar.Iga.Api;
using Northstar.Iga.Application;
using Northstar.Iga.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        if (context.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
        {
            context.ProblemDetails.Extensions["correlationId"] = correlationId?.ToString();
        }
    };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddIgaInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.RemoveAll<IAuditContextAccessor>();
builder.Services.AddScoped<IAuditContextAccessor, HttpAuditContextAccessor>();
builder.Services.AddScoped<ITokenIssuer, JwtTokenIssuer>();

var configuredJwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (builder.Environment.IsProduction() &&
    configuredJwt.SigningKey.StartsWith("dev-only-not-a-real-secret", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Production startup refused: replace the development-only JWT signing key.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = builder.Environment.IsProduction();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuredJwt.Issuer,
            ValidateAudience = true,
            ValidAudience = configuredJwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuredJwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(15),
            NameClaimType = "sub"
        };        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Authentication required",
                    Detail = "A valid bearer token is required.",
                    Instance = context.Request.Path
                };
                problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                await context.Response.WriteAsJsonAsync(problem);
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Insufficient permissions",
                    Detail = "The authenticated principal does not have the required IGA scope.",
                    Instance = context.Request.Path
                };
                problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                await context.Response.WriteAsJsonAsync(problem);
            }
        };
    });

// The token issuer stamps nbf and exp from IClock, so the validator has to read the same
// clock or the two halves of the auth path disagree about what time it is. The default
// lifetime validator reads DateTime.UtcNow, which is correct in production -- where IClock
// *is* the system clock -- and wrong everywhere the clock is controlled.
//
// This is a post-configure rather than part of the block above because
// TokenValidationParameters is built before the service provider exists.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IClock>((options, clock) =>
    {
        options.TokenValidationParameters.LifetimeValidator =
            (notBefore, expires, _, parameters) =>
            {
                var now = clock.UtcNow.UtcDateTime;
                var skew = parameters.ClockSkew;
                if (notBefore is not null && now.Add(skew) < notBefore.Value) return false;
                if (expires is not null && now.Subtract(skew) > expires.Value) return false;
                return true;
            };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ApiPolicies.Read, policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => context.User.HasScope("iga.read") || context.User.HasScope("iga.admin")));
    options.AddPolicy(ApiPolicies.Admin, policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => context.User.HasScope("iga.admin")));
    options.AddPolicy(ApiPolicies.Approve, policy => policy.RequireAuthenticatedUser().RequireAssertion(
        context => context.User.HasScope("iga.approve") || context.User.HasScope("iga.admin")));
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection(CorsOptions.SectionName)
            .Get<CorsOptions>()?.AllowedOrigins ?? ["http://localhost:5024"];
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        context.HttpContext.Response.Headers.RetryAfter = "60";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Rate limit exceeded",
            Detail = "Retry the request after the current one-minute window.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    };
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("sqlite", tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource(IgaTelemetry.MeterName).AddAspNetCoreInstrumentation();
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(IgaTelemetry.MeterName).AddAspNetCoreInstrumentation();
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            metrics.AddConsoleExporter();
        }
    });
builder.Services.AddHostedService<ElevationExpiryWorker>();
builder.Services.AddHostedService<CampaignDeadlineWorker>();
builder.Services.AddHostedService<ApprovalEscalationWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Northstar IGA API v1");
        options.DocumentTitle = "Northstar IGA API";
    });
}
app.MapAuthEndpoints();
app.MapIdentityCatalogEndpoints();
app.MapPolicyAndSodEndpoints();
app.MapRequestElevationCampaignEndpoints();
app.MapProvisioningAndReportEndpoints();
app.MapScimEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await initializer.InitializeAsync(app.Environment.IsDevelopment(), CancellationToken.None);
}

app.Run();

public partial class Program;
