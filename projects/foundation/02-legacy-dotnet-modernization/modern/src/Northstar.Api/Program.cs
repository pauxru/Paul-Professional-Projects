using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Northstar.Api.Endpoints;
using Northstar.Api.Options;
using Northstar.Api.Security;
using Northstar.Application.Abstractions;
using Northstar.Application.Claims;
using Northstar.Application.Importing;
using Northstar.Application.Observability;
using Northstar.Domain.Claims;
using Northstar.Infrastructure.Documents;
using Northstar.Infrastructure.Importing;
using Northstar.Infrastructure.Persistence;
using Northstar.Infrastructure.Time;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DocumentStoreOptions>()
    .Bind(builder.Configuration.GetSection(DocumentStoreOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<LegacyImportOptions>()
    .Bind(builder.Configuration.GetSection(LegacyImportOptions.SectionName))
    .ValidateOnStart();

var jwtSettings = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (builder.Environment.IsProduction() && jwtSettings.SigningKey.StartsWith("dev-only-not-a-real-secret", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The development JWT signing key cannot be used in Production.");
}

builder.Services.AddDbContext<NorthstarDbContext>((services, options) =>
{
    var database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    if (string.Equals(database.Provider, "Npgsql", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(database.ConnectionString);
    }
    else
    {
        options.UseSqlite(database.ConnectionString);
    }
});
builder.Services.AddScoped<IClaimsStore, EfClaimsStore>();
builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
builder.Services.AddScoped<ClaimApplicationService>();
builder.Services.AddScoped<LegacyClaimImporter>();
builder.Services.AddSingleton<SettlementCalculator>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IDocumentStore, LocalFileDocumentStore>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddSingleton<ILegacyClaimSource>(services =>
    new LegacySqliteClaimSource(services.GetRequiredService<IOptions<LegacyImportOptions>>().Value.ConnectionString));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization(options =>
{
    AddScopePolicy(options, "claims:read");
    AddScopePolicy(options, "claims:adjust");
    AddScopePolicy(options, "claims:approve");
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("api", limiter =>
    {
        limiter.PermitLimit = 100;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(NorthstarTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(NorthstarTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<NorthstarDbContext>();
    await dbContext.Database.MigrateAsync();
    await DemoDataSeeder.SeedAsync(dbContext, scope.ServiceProvider.GetRequiredService<IClock>(), CancellationToken.None);
}

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next(context);
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    if (context.Request.IsHttps)
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }

    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Northstar Claims API v1");
    });
}
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapOpenApi();
app.MapAuthEndpoints();
app.MapClaimsEndpoints();
app.Run();

static void AddScopePolicy(AuthorizationOptions options, string scope) =>
    options.AddPolicy(scope, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.Claims
            .Where(claim => claim.Type is "scope" or "scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal)));

public partial class Program;
