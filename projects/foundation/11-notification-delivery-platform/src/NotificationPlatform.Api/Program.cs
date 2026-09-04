using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Api.Endpoints;
using NotificationPlatform.Api.Middleware;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Infrastructure;
using NotificationPlatform.Infrastructure.Metrics;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Seed;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console();
});

// Bind + validate typed configuration
builder.Services.AddNotificationPlatform(builder.Configuration);

// DbContext (pooled factory)
var dbSection = builder.Configuration.GetSection(DatabaseOptions.SectionName);
var provider = dbSection["Provider"] ?? "Sqlite";
var connStr = dbSection["ConnectionString"] ?? "Data Source=notifications.db";
if (!provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Only Sqlite is enabled in this build; requested '{provider}'.");
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connStr));
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connStr));

// Delivery hosted worker unless in Testing environment
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDeliveryHostedWorker();
}

// Auth
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtOptionsAtStartup = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
var isTesting = builder.Environment.IsEnvironment("Testing");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = !isTesting,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptionsAtStartup.Issuer,
            ValidAudience = jwtOptionsAtStartup.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptionsAtStartup.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(options =>
{
    void Policy(string name, string scope) => options.AddPolicy(name, p => p.RequireAssertion(ctx =>
        ctx.User.Identity?.IsAuthenticated == true && ctx.User.HasClaim(c => (c.Type == "scp" || c.Type == "scope") && c.Value.Split(' ').Contains(scope))));

    Policy(Policies.SendNotifications, Policies.SendNotifications);
    Policy(Policies.ManageTemplates, Policies.ManageTemplates);
    Policy(Policies.ManagePreferences, Policies.ManagePreferences);
    Policy(Policies.ManageSuppressions, Policies.ManageSuppressions);
    Policy(Policies.ManageDlq, Policies.ManageDlq);
    Policy(Policies.ViewAnalytics, Policies.ViewAnalytics);
    Policy(Policies.IngestReceipts, Policies.IngestReceipts);
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3011").AllowAnyHeader().AllowAnyMethod()));

// OpenTelemetry (skip console exporter in Testing to keep test output clean)
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource(NotificationMetrics.ActivitySourceName).AddConsoleExporter())
        .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter(NotificationMetrics.MeterName).AddConsoleExporter());
}
else
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource(NotificationMetrics.ActivitySourceName))
        .WithMetrics(m => m.AddMeter(NotificationMetrics.MeterName));
}

var app = builder.Build();

// Guard: refuse to boot Production with the default dev signing key
if (app.Environment.IsProduction())
{
    var jwt = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
    if (jwt.SigningKey.StartsWith("dev-only-", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to start in Production with the default dev signing key.");
    var wh = app.Services.GetRequiredService<IOptions<WebhookOptions>>().Value;
    if (wh.SigningKey.StartsWith("dev-only-", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to start in Production with the default dev webhook signing key.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseCors();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi();

app.MapAuthEndpoints();
app.MapNotificationEndpoints();
app.MapTemplateEndpoints();
app.MapPreferenceEndpoints();
app.MapSuppressionEndpoints();
app.MapUnsubscribeEndpoints();
app.MapDlqEndpoints();
app.MapAnalyticsEndpoints();
app.MapReceiptEndpoints();

// Simple dashboard (static)
app.MapGet("/", () => Results.Redirect("/dashboard/index.html"));
app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure database + seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (!app.Environment.IsEnvironment("Testing"))
    {
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
        await SeedData.EnsureSeedAsync(db, clock, ids, logger, CancellationToken.None);
    }
}

app.Run();

public partial class Program;
