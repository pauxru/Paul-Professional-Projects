using System.Text;
using System.Threading.RateLimiting;
using Idp.Api.Auth;
using Idp.Api.Endpoints;
using Idp.Api.Observability;
using Idp.Application;
using Idp.Application.Configuration;
using Idp.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---- Logging (Serilog) ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---- Application + Infrastructure ----------------------------------------------------------------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- Upload limits -------------------------------------------------------------------------------
// Kestrel/form limits are a coarse outer bound; DocumentIntakeService enforces the precise
// per-file MaxUploadBytes and returns a 400 ProblemDetails so oversize uploads fail cleanly.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 16 * 1024 * 1024;
    o.ValueLengthLimit = 16 * 1024 * 1024;
});

// ---- AuthN / AuthZ -------------------------------------------------------------------------------
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Permissions.DocumentsSubmit, p =>
        p.RequireClaim(Permissions.ClaimType, Permissions.DocumentsSubmit))
    .AddPolicy(Permissions.ReviewProcess, p =>
        p.RequireClaim(Permissions.ClaimType, Permissions.ReviewProcess))
    .AddPolicy(Permissions.ReviewApprove, p =>
        p.RequireClaim(Permissions.ClaimType, Permissions.ReviewApprove))
    .AddPolicy(Permissions.ExportManage, p =>
        p.RequireClaim(Permissions.ClaimType, Permissions.ExportManage));

// ---- ProblemDetails, OpenAPI, rate limiting ------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// ---- OpenTelemetry -------------------------------------------------------------------------------
builder.Services.AddSingleton<IdpMetrics>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("idp-api"))
    .WithMetrics(m =>
    {
        m.AddMeter(IdpMetrics.MeterName);
        m.AddAspNetCoreInstrumentation();
        if (builder.Environment.IsDevelopment())
            m.AddConsoleExporter();
    })
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        if (builder.Environment.IsDevelopment())
            t.AddConsoleExporter();
    });

var app = builder.Build();

// Force the metrics singleton to instantiate so its instruments/gauges are registered.
_ = app.Services.GetRequiredService<IdpMetrics>();

// ---- Middleware pipeline -------------------------------------------------------------------------
app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// ---- Endpoints -----------------------------------------------------------------------------------
var v1 = app.MapGroup("/api/v1");
v1.MapDocumentEndpoints();
v1.MapReviewEndpoints();
v1.MapExportEndpoints();
v1.MapSupplierEndpoints();
app.MapSystemEndpoints();

// ---- Database create + optional demo seed --------------------------------------------------------
var seed = app.Configuration.GetValue("Seed", app.Environment.IsDevelopment());
await InfrastructureServiceCollectionExtensions.InitializeDatabaseAsync(app.Services, seed);

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can bootstrap the API.</summary>
public partial class Program;
