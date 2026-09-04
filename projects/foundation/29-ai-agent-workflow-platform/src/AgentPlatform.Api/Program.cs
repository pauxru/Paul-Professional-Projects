using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgentPlatform.Api.Endpoints;
using AgentPlatform.Api.Observability;
using AgentPlatform.Api.Security;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Diagnostics;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Seeding;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var isTesting = builder.Environment.EnvironmentName == "Testing";

// --- Logging (structured, deterministic sink in tests) ----------------------
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .WriteTo.Console());

// --- Platform composition root ----------------------------------------------
builder.Services.AddAgentPlatform(builder.Configuration);

// Real metrics implementation (overrides the null default registered above).
var metrics = new AgentMetrics();
builder.Services.AddSingleton(metrics);
builder.Services.AddSingleton<IAgentMetrics>(metrics);

// --- Security: JWT bearer + scope policies -----------------------------------
var jwtOptions = new JwtOptions();
builder.Configuration.GetSection("Jwt").Bind(jwtOptions);
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<TokenService>();
var tokenService = new TokenService(jwtOptions);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep original claim types ("sub", "scope", "tenant") instead of SOAP URIs.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = tokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = tokenService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = tokenService.SigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AgentPolicies.Run, p => p.RequireAssertion(ctx => CallerFactory.ScopesOf(ctx.User).Contains(AgentPolicies.Run)))
    .AddPolicy(AgentPolicies.Approve, p => p.RequireAssertion(ctx => CallerFactory.ScopesOf(ctx.User).Contains(AgentPolicies.Approve)))
    .AddPolicy(AgentPolicies.Admin, p => p.RequireAssertion(ctx => CallerFactory.ScopesOf(ctx.User).Contains(AgentPolicies.Admin)));

// --- Problem details + typed exception mapping -------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

// --- Rate limiting (per tenant, fixed window) --------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User.FindFirst(AgentPolicies.TenantClaim)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

// --- OpenAPI + JSON ----------------------------------------------------------
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// --- Health checks -----------------------------------------------------------
builder.Services.AddHealthChecks();

// --- OpenTelemetry (span tree mirrors the trace; disabled under test) --------
if (!isTesting)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t
            .AddSource(AgentTelemetry.SourceName)
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter())
        .WithMetrics(m => m
            .AddMeter(AgentMetrics.MeterName)
            .AddConsoleExporter());
}

var app = builder.Build();

// --- Middleware pipeline (order matters) -------------------------------------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RequestContextMiddleware>();
if (!isTesting) app.UseSerilogRequestLogging();

app.UseDefaultFiles();
app.UseStaticFiles();

if (!isTesting) app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

// --- Endpoints ---------------------------------------------------------------
app.MapAgentPlatformApi();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", async (AgentDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .WithTags("Ops");

// --- Database bootstrap + deterministic seed ---------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DatabaseSeeder.SeedAsync(db);
}

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host the API in tests.</summary>
public partial class Program;
