using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LoanOrigination.Api.Configuration;
using LoanOrigination.Api.Endpoints;
using LoanOrigination.Application.Ports;
using LoanOrigination.Application.Services;
using LoanOrigination.Domain.Rules;
using LoanOrigination.Infrastructure;
using LoanOrigination.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ObjectStorageOptions>()
    .Bind(builder.Configuration.GetSection(ObjectStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var objectStorageOptions = builder.Configuration.GetSection(ObjectStorageOptions.SectionName).Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var webhookOptions = builder.Configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>() ?? new WebhookOptions();
var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();
if (builder.Environment.IsProduction() &&
    (jwtOptions.SigningKey == JwtOptions.DefaultSigningKey || webhookOptions.SigningSecret == WebhookOptions.DefaultSigningSecret))
{
    throw new InvalidOperationException("Production refuses development JWT or webhook signing keys.");
}

var objectStorePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, objectStorageOptions.RootPath));
builder.Services.AddLoanInfrastructure(databaseOptions.ConnectionString, objectStorePath);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITokenIssuer>(serviceProvider =>
    new LocalJwtTokenIssuer(jwtOptions, serviceProvider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IWebhookReplayStore, InMemoryWebhookReplayStore>();
builder.Services.AddSingleton<DeclarativeRulesEngine>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<RulesetService>();
builder.Services.AddScoped<LoanApplicationService>();
builder.Services.AddScoped<UnderwritingService>();
builder.Services.AddScoped<OfferService>();
builder.Services.AddScoped<DisbursementService>();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["correlationId"] = RequestMetadata.CorrelationId(context.HttpContext);
    };
});
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<SqliteReadinessHealthCheck>("sqlite-ready", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("api", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));
});
builder.Services.AddCors(options => options.AddPolicy("configured", policy =>
    policy.WithOrigins(corsOptions.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
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
            ClockSkew = TimeSpan.Zero,
            NameClaimType = "name"
        };
    });
builder.Services.AddAuthorization(options =>
{
    foreach (var scope in new[] { ScopePolicies.Apply, ScopePolicies.Underwrite, ScopePolicies.Approve, ScopePolicies.Admin })
    {
        options.AddPolicy(scope, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => ScopePolicies.HasScope(context.User, scope)));
    }
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("RiftValleyCredit.LoanOrigination")
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(LoanTelemetry.MeterName)
        .AddConsoleExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId(app.Logger);
app.UsePlatformSecurityHeaders();
app.UseCors("configured");
app.UseRateLimiter();
app.UseMiddleware<ProviderCallbackSignatureMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapOpenApi();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Loan Origination API v1");
    });
}

app.MapGet("/", () => Results.Ok(new { service = "Rift Valley Credit Ltd (fictional) Loan Origination API", version = "v1" }))
    .AllowAnonymous();
app.MapAuthEndpoints(app.Environment);
app.MapCustomerEndpoints();
app.MapProductEndpoints();
app.MapRulesetEndpoints();
app.MapApplicationEndpoints();
app.MapUnderwritingEndpoints();
app.MapOfferEndpoints();
app.MapDisbursementEndpoints();
app.MapAuditEndpoints();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<LoanDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await DemoDataSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<ILoanRepository>(), DateTimeOffset.UtcNow, CancellationToken.None);
}

app.Run();

public partial class Program;
