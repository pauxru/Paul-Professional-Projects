using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Lab.Application.Abstractions;
using Lab.Application.Contracts;
using Lab.Diagnostics.Measurement;
using Lab.Infrastructure;
using Lab.Infrastructure.Persistence;
using Lab.SampleApp.Endpoints;
using Lab.SampleApp.Middleware;
using Microsoft.AspNetCore.RateLimiting;
using Lab.SampleApp.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

if (builder.Environment.IsProduction() &&
    string.Equals(jwtOptions.SigningKey, JwtOptions.DefaultDevelopmentSigningKey, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Production startup is blocked while the default development JWT signing key is configured.");
}

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddNorthstarInfrastructure(databaseOptions);
builder.Services.AddSingleton<ITokenIssuer, DevelopmentTokenIssuer>();
builder.Services.AddHealthChecks().AddCheck<DatabaseReadyHealthCheck>("database");
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(10)
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("orders.read", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, "orders.read")));
    options.AddPolicy("orders.write", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, "orders.write")));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("northstar-api", limiterOptions =>
    {
        limiterOptions.PermitLimit = 100;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Northstar Logistics (fictional)"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource(LabTelemetry.ActivitySourceName)
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(LabTelemetry.MeterName)
        .AddConsoleExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();
app.UseSecurityHeaders();
app.UseStatusCodePages(StatusCodeProblemDetails.WriteAsync);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi("/openapi/{documentName}.json");

if (!app.Environment.IsProduction())
{
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("/openapi/v1.json", "Northstar Logistics v1");
    });
    app.MapAuthEndpoints();
}

app.MapOrderEndpoints();

if (app.Environment.IsDevelopment())
{
    using var seedBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await NorthstarSeeder.EnsureCreatedAndSeedAsync(app.Services, DateTimeOffset.UtcNow, seedBudget.Token);
}

app.Run();

static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string requiredScope) =>
    user.FindAll("scope")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Contains(requiredScope, StringComparer.Ordinal);

public partial class Program;
