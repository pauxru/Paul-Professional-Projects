using System.Text;
using ExampleBank.Ledger.Api.Auth;
using ExampleBank.Ledger.Api.Contracts;
using ExampleBank.Ledger.Api.Endpoints;
using ExampleBank.Ledger.Api.Middleware;
using ExampleBank.Ledger.Application;
using ExampleBank.Ledger.Infrastructure;
using ExampleBank.Ledger.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.WebHost.UseUrls("http://localhost:5013");

// ----- Configuration -----
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<DevTokenService>();

var connectionString = builder.Configuration.GetConnectionString("Ledger") ?? "Data Source=ledger.db";

// ----- Authentication & authorization -----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(LedgerPolicies.Read, p => p.RequireAuthenticatedUser().RequireClaim("scope", LedgerPolicies.Read))
    .AddPolicy(LedgerPolicies.Post, p => p.RequireAuthenticatedUser().RequireClaim("scope", LedgerPolicies.Post))
    .AddPolicy(LedgerPolicies.Adjust, p => p.RequireAuthenticatedUser().RequireClaim("scope", LedgerPolicies.Adjust))
    .AddPolicy(LedgerPolicies.Admin, p => p.RequireAuthenticatedUser().RequireClaim("scope", LedgerPolicies.Admin));

// ----- Problem details & exception handling -----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();

// ----- OpenAPI -----
builder.Services.AddOpenApi();

// ----- Application & infrastructure -----
builder.Services.AddLedgerApplication();
builder.Services.AddLedgerInfrastructure(connectionString);

// ----- OpenTelemetry (metrics + tracing) -----
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ExampleBank.Ledger.Api"))
    .WithMetrics(metrics => metrics
        .AddMeter(LedgerMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation());

var app = builder.Build();

// ----- Initialise database (create schema + seed chart of accounts) -----
// Skipped under the integration-test host, which initialises the database exactly once itself
// (running it here as well would race two seeders against the same SQLite file).
if (!app.Environment.IsEnvironment("Testing"))
{
    await app.Services.InitializeLedgerDatabaseAsync();
}

// ----- Pipeline -----
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();

app.MapLedgerEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ExampleBank.Ledger", utc = DateTimeOffset.UtcNow }))
    .WithTags("Health").AllowAnonymous();

// Development-only token minting endpoint (guarded by configuration).
// Read from app.Configuration (post-build) so the integration-test host can enable it.
if (app.Configuration.GetValue<bool>("Auth:EnableDevTokens"))
{
    app.MapPost("/api/v1/dev/token", (DevTokenBody body, DevTokenService tokens) =>
    {
        var scopes = body.Scopes is { Length: > 0 } ? body.Scopes : LedgerPolicies.All;
        return Results.Ok(new { access_token = tokens.CreateToken(body.Subject, scopes), token_type = "Bearer" });
    }).WithTags("Dev").AllowAnonymous();
}

app.Run();

/// <summary>Exposed so the integration-test host (WebApplicationFactory) can bootstrap the API.</summary>
public partial class Program;
