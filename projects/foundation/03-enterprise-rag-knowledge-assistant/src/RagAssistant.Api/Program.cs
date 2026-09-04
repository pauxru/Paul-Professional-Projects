using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RagAssistant.Api.Auth;
using RagAssistant.Api.Endpoints;
using RagAssistant.Api.Middleware;
using RagAssistant.Infrastructure;
using RagAssistant.Infrastructure.Options;
using RagAssistant.Infrastructure.Persistence;
using RagAssistant.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        var env = ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (env.IsEnvironment("Testing") || env.IsDevelopment())
        {
            var feature = ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            if (feature?.Error is { } ex)
            {
                ctx.ProblemDetails.Extensions["exception"] = ex.GetType().FullName;
                ctx.ProblemDetails.Extensions["exceptionMessage"] = ex.Message;
            }
        }
    };
});
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<JwtOptions>>().Value);
builder.Services.AddSingleton<IDevTokenIssuer, HmacDevTokenIssuer>();
builder.Services.AddSingleton<IUserPrincipalAccessor, ClaimsUserPrincipalAccessor>();

builder.Services.AddRagInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(15),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("KnowledgeReader", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("KnowledgeAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.HasClaim("role", "admin")
            || ctx.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "admin"));
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User?.Identity?.Name
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        var permits = context.RequestServices.GetService<IConfiguration>()?.GetValue<int?>("RateLimiting:PermitsPerMinute") ?? 120;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
        });
    });
});

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddMeter("RagAssistant");
        metrics.AddConsoleExporter();
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource("RagAssistant");
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddConsoleExporter();
    });

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(new Meter("RagAssistant"));

builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5003").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
}
else
{
    var jwt = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
    if (jwt.SigningKey.StartsWith("dev-only", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Refusing to start in Production with default dev JWT signing key.");
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapAuthEndpoints();
app.MapDocumentEndpoints();
app.MapQueryEndpoints();
app.MapChatEndpoints();
app.MapFeedbackEndpoints();
app.MapPromptEndpoints();
app.MapEvaluationEndpoints();

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RagDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    if (!app.Environment.IsProduction())
    {
        var seeder = scope.ServiceProvider.GetRequiredService<CorpusSeeder>();
        await seeder.SeedAsync(CancellationToken.None);
    }
}

app.Run();

public partial class Program;
