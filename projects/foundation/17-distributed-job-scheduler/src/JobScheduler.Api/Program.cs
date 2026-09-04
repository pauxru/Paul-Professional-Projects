using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using JobScheduler.Api.Auth;
using JobScheduler.Api.Dashboard;
using JobScheduler.Api.Endpoints;
using JobScheduler.Api.Middleware;
using JobScheduler.Api.Observability;
using JobScheduler.Application.Options;
using JobScheduler.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration -------------------------------------------------------
var connectionString = builder.Configuration.GetValue<string>("Database:ConnectionString")
                       ?? "Data Source=jobscheduler.db;Cache=Shared";

// ---- Services ------------------------------------------------------------
builder.Services.AddSchedulerSqlite(connectionString);
builder.Services.AddSchedulerCore(builder.Configuration);

var nodeOptions = builder.Configuration.GetSection(NodeOptions.SectionName).Get<NodeOptions>() ?? new NodeOptions();
if (nodeOptions.RunWorker || nodeOptions.RunLeader)
{
    builder.Services.AddSchedulerEngine();
}

builder.Services.AddSchedulerAuth(builder.Configuration);
builder.Services.AddSchedulerTelemetry(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

var app = builder.Build();

// ---- Startup guards + schema/seed ---------------------------------------
app.Services.GuardSigningKey(app.Environment);

bool seedDemo = app.Configuration.GetValue("Seed:DemoData", true);
await app.Services.InitializeSchedulerAsync(seedDemo);

// ---- Middleware pipeline -------------------------------------------------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ---- Endpoints -----------------------------------------------------------
app.MapOpenApi();
app.MapAuthEndpoints();
app.MapJobsEndpoints();
app.MapRunsEndpoints();
app.MapWorkersEndpoints();
app.MapDlqEndpoints();
app.MapScheduleEndpoints();
app.MapLeaderEndpoints();
app.MapHealthEndpoints();
app.MapDashboard();

app.Run();

/// <summary>Exposed so the integration test project can boot the API with WebApplicationFactory.</summary>
public partial class Program;
