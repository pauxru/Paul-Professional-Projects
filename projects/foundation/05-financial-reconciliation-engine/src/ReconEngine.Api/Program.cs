using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReconEngine.Api.Auth;
using ReconEngine.Api.Endpoints;
using ReconEngine.Api.Middleware;
using ReconEngine.Api.Observability;
using ReconEngine.Application;
using ReconEngine.Application.Common;
using ReconEngine.Application.Observability;
using ReconEngine.Infrastructure;
using ReconEngine.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---- options ----
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.AddOptions<ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(ReconciliationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---- application + infrastructure ----
var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=recon.db";
builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);

builder.Services.AddSingleton<ReconMetrics>();
builder.Services.AddScoped<TokenIssuer>();

// ---- JSON: serialize enums as strings ----
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- problem details + global exception handling ----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ---- authentication + authorization ----
var jwt = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ReconScopes.Run, p => p.RequireAuthenticatedUser().RequireClaim("scope", ReconScopes.Run))
    .AddPolicy(ReconScopes.Resolve, p => p.RequireAuthenticatedUser().RequireClaim("scope", ReconScopes.Resolve))
    .AddPolicy(ReconScopes.Approve, p => p.RequireAuthenticatedUser().RequireClaim("scope", ReconScopes.Approve));

// ---- rate limiting ----
var permit = builder.Configuration.GetValue("RateLimiting:PermitPerWindow", 100);
var windowSeconds = builder.Configuration.GetValue("RateLimiting:WindowSeconds", 10);
var queueLimit = builder.Configuration.GetValue("RateLimiting:QueueLimit", 0);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
});

// ---- observability ----
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ReconEngine.Api"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddSource(ReconDiagnostics.SourceName);
        if (builder.Environment.IsDevelopment())
            t.AddConsoleExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation().AddMeter(ReconMetrics.MeterName);
        if (builder.Environment.IsDevelopment())
            m.AddConsoleExporter();
    });

// ---- health + openapi ----
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
builder.Services.AddOpenApi();

var app = builder.Build();

// ---- middleware pipeline ----
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready");

app.MapAuthEndpoints();
app.MapImportEndpoints();
app.MapRuleSetEndpoints();
app.MapRunEndpoints();
app.MapExceptionEndpoints();
app.MapReportEndpoints();

// ---- database bootstrap (idempotent) ----
await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

/// <summary>Exposed so integration tests can spin up the API with WebApplicationFactory.</summary>
public partial class Program;
