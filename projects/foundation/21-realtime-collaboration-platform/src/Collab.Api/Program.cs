using System.Text;
using System.Threading.RateLimiting;
using Collab.Api.Auth;
using Collab.Api.Common;
using Collab.Api.Endpoints;
using Collab.Api.Realtime;
using Collab.Application;
using Collab.Application.Abstractions;
using Collab.Application.Options;
using Collab.Infrastructure;
using Collab.Infrastructure.Observability;
using Collab.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var isTesting = builder.Environment.IsEnvironment("Testing");

// ---- Options -------------------------------------------------------------------------------------
builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
builder.Services.Configure<CollaborationOptions>(config.GetSection(CollaborationOptions.SectionName));

// ---- Layers --------------------------------------------------------------------------------------
var connectionString = config.GetConnectionString("Default") ?? "Data Source=collab.db";
builder.Services.AddCollabInfrastructure(connectionString);
builder.Services.AddCollabApplication();

// The Application pushes to clients through this SignalR-backed adapter.
builder.Services.AddScoped<IClientNotifier, SignalRClientNotifier>();

// ---- Authentication / Authorization ---------------------------------------------------------------
var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5),
            NameClaimType = "name",
            RoleClaimType = "role"
        };

        // Browsers can't set Authorization headers on the WebSocket handshake, so accept the token
        // from the access_token query string, but only for the hub path.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/collaboration"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// ---- Realtime -------------------------------------------------------------------------------------
builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment() || isTesting);
builder.Services.AddSingleton<HubRateLimiter>();
builder.Services.AddHostedService<PresenceFlusherService>();

// ---- Web/API cross-cutting ------------------------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddCors(o => o.AddPolicy("web", p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        // Never throttle the realtime hub here — the hub has its own per-connection limiter.
        if (ctx.Request.Path.StartsWithSegments("/hubs"))
            return RateLimitPartition.GetNoLimiter("hub");

        var key = ctx.User?.FindFirst("sub")?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1000,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// ---- OpenTelemetry --------------------------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("collab-api"))
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation();
        m.AddMeter(CollabMetrics.MeterName);
        if (!isTesting)
            m.AddConsoleExporter();
    })
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        if (!isTesting)
            t.AddConsoleExporter();
    });

var app = builder.Build();

// ---- Schema + demo seed (SQLite, no external infra) ----------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<CollabDbInitializer>();
    var seedDemo = config.GetValue("Seed:Demo", true);
    await initializer.InitializeAsync(seedDemo, CancellationToken.None);
}

// ---- Middleware pipeline (no HTTPS redirect: this is an HTTP demo host) ---------------------------
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// ---- Endpoints ------------------------------------------------------------------------------------
app.MapAuthEndpoints();
app.MapWorkspaceEndpoints();
app.MapDocumentEndpoints();
app.MapCommentEndpoints();
app.MapNotificationEndpoints();

app.MapHub<CollaborationHub>("/hubs/collaboration");

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithTags("Health")
    .AllowAnonymous();

app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .WithTags("Health")
    .AllowAnonymous();

app.Run();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can bootstrap the app.</summary>
public partial class Program;
