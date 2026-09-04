using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ZeroTrust.Api.Authorization;
using ZeroTrust.Api.Configuration;
using ZeroTrust.Api.Endpoints;
using ZeroTrust.Api.Middleware;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure;
using ZeroTrust.Infrastructure.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;
using ZeroTrust.Infrastructure.Time;

// Disable the default inbound/outbound claim type remapping so `sub`/`amr`/`acr`/`email`
// stay as-is on the ClaimsPrincipal (otherwise `FindFirst("amr")` returns null because
// the framework rewrites the type to a schemas.xmlsoap URI).
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
JwtSecurityTokenHandler.DefaultOutboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

// ----- Options -----
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TokenIssuerOptions>()
    .Bind(builder.Configuration.GetSection(TokenIssuerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName));
builder.Services.AddOptions<RateLimitingOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();

// Production guard: refuse to boot with default dev signing key.
if (builder.Environment.IsProduction())
{
    var jwt = builder.Configuration.GetSection(TokenIssuerOptions.SectionName).Get<TokenIssuerOptions>()
        ?? new TokenIssuerOptions();
    if (jwt.HsSigningKey.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to start in Production with default dev signing key.");
}

// ----- Persistence -----
var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
    ?? new DatabaseOptions();
builder.Services.AddDbContext<ZeroTrustDbContext>(o => o.UseSqlite(dbOptions.ConnectionString));

// ----- App services -----
builder.Services.AddSingleton<IClock, ZeroTrust.Infrastructure.Time.SystemClock>();
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<ITokenIssuer, TokenIssuer>();
builder.Services.AddScoped<ITokenValidator, TokenValidator>();
builder.Services.AddScoped<IJwksProvider, JwksProvider>();
builder.Services.AddSingleton<IWebhookSigner, HmacWebhookSigner>();
builder.Services.AddSingleton<IWebhookVerifier, HmacWebhookVerifier>();
builder.Services.AddScoped<AuthorizationExplainer>();
builder.Services.AddSingleton<IApiKeyToggle, ApiKeyToggle>();

// ----- Authentication -----
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "MultiScheme";
        options.DefaultChallengeScheme = "MultiScheme";
    })
    .AddPolicyScheme("MultiScheme", "Multi", options =>
    {
        options.ForwardDefaultSelector = ctx =>
        {
            var apiKey = ctx.Request.Headers[new ApiKeyAuthenticationOptions().HeaderName].ToString();
            if (!string.IsNullOrEmpty(apiKey)) return ApiKeyAuthenticationOptions.SchemeName;
            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.SchemeName,
        options => { })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        // Custom validation via ITokenValidator on each request through OnMessageReceived / OnTokenValidated hook.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = async ctx =>
            {
                string? bearer = null;
                if (ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    bearer = ctx.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
                }
                if (string.IsNullOrEmpty(bearer))
                {
                    ctx.NoResult();
                    return;
                }

                var expectedAudience = ResolveAudienceForPath(ctx.Request.Path);
                var validator = ctx.HttpContext.RequestServices.GetRequiredService<ITokenValidator>();
                var outcome = await validator.ValidateAsync(bearer, expectedAudience, ctx.HttpContext.RequestAborted);
                if (!outcome.IsValid)
                {
                    ctx.Fail(outcome.FailureReason ?? "invalid_token");
                    return;
                }
                var identity = new System.Security.Claims.ClaimsIdentity("Bearer");
                if (outcome.Subject is not null) identity.AddClaim(new("sub", outcome.Subject));
                foreach (var s in outcome.Scopes) { }
                if (outcome.Scopes.Count > 0) identity.AddClaim(new("scope", string.Join(' ', outcome.Scopes)));
                foreach (var r in outcome.Roles) identity.AddClaim(new("roles", r));
                if (outcome.Amr is not null) identity.AddClaim(new("amr", outcome.Amr));
                if (outcome.Acr is not null) identity.AddClaim(new("acr", outcome.Acr));
                if (outcome.Audience is not null) identity.AddClaim(new("aud", outcome.Audience));
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(bearer);
                var typ = jwt.Claims.FirstOrDefault(c => c.Type == "typ")?.Value;
                if (typ is not null) identity.AddClaim(new("typ", typ));
                var partnerCode = jwt.Claims.FirstOrDefault(c => c.Type == "partner_code")?.Value;
                if (partnerCode is not null) identity.AddClaim(new("partner_code", partnerCode));

                ctx.Principal = new System.Security.Claims.ClaimsPrincipal(identity);
                ctx.Success();
            }
        };
    });

// ----- Authorization -----
builder.Services.AddScoped<IAuthorizationHandler, ScopeHandler>();
builder.Services.AddScoped<IAuthorizationHandler, RoleHandler>();
builder.Services.AddScoped<IAuthorizationHandler, StepUpHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ClientTypeHandler>();
builder.Services.AddScoped<IAuthorizationHandler, AccountOwnerHandler>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("customer.read", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.CustomerRead))
        .RequireClaim("aud", Audience.Customer));

    options.AddPolicy("customer.write", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.CustomerWrite))
        .RequireClaim("aud", Audience.Customer));

    options.AddPolicy("partner.payments.initiate", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.PartnerPaymentsInitiate))
        .RequireClaim("aud", Audience.Partner));

    options.AddPolicy("partner.payments.read", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.PartnerPaymentsRead))
        .RequireClaim("aud", Audience.Partner));

    options.AddPolicy("admin.audit", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.AdminAudit),
            new StepUpRequirement("mfa", "urn:ntsf:acr:step-up"))
        .RequireClaim("aud", Audience.Admin));

    options.AddPolicy("admin.users", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.AdminUsers),
            new RoleRequirement("admin"),
            new StepUpRequirement("mfa", "urn:ntsf:acr:step-up"))
        .RequireClaim("aud", Audience.Admin));

    options.AddPolicy("admin.authz.evaluate", p => p.RequireAuthenticatedUser()
        .AddRequirements(new ScopeRequirement(Scope.AdminAuthzEvaluate),
            new StepUpRequirement("mfa", "urn:ntsf:acr:step-up"))
        .RequireClaim("aud", Audience.Admin));
});

// ----- Rate limiting -----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers["Retry-After"] = "60";
        var cid = ctx.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? "-";
        var audit = ctx.HttpContext.RequestServices.GetRequiredService<IAuditLog>();
        await audit.AppendAsync(AuditKind.RateLimitTriggered,
            ctx.HttpContext.User.Identity?.Name ?? "-", "rate_limit", ctx.HttpContext.Request.Path,
            cid, ctx.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-",
            ctx.HttpContext.Request.Headers.UserAgent.ToString(), "429", false, ct);
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "about:blank", title = "Too Many Requests", status = 429,
            detail = "Rate limit exceeded. See Retry-After header.", traceId = cid
        }, cancellationToken: ct);
    };
    options.AddPolicy("partner-token-bucket", ctx =>
    {
        var permits = ctx.Items["PartnerRateLimit"] is int r ? r
            : ctx.User.FindFirst("partner_code") is not null
                ? builder.Configuration.GetValue("RateLimiting:PartnerPermitsPerMinute", 60)
                : 20;
        var partitionKey = ctx.User.FindFirst("partner_code")?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "public";
        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(2, permits),
            TokensPerPeriod = Math.Max(1, permits / 4),
            ReplenishmentPeriod = TimeSpan.FromSeconds(15),
            AutoReplenishment = true,
            QueueLimit = 0
        });
    });
    options.AddPolicy("admin-fixed-window", ctx =>
    {
        var permits = builder.Configuration.GetValue("RateLimiting:AdminPermitsPerMinute", 30);
        var partitionKey = ctx.User.FindFirst("sub")?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "admin-public";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// ----- CORS -----
var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();
builder.Services.AddCors(o => o.AddPolicy("default", p =>
{
    if (corsOptions.AllowedOrigins.Length > 0)
    {
        p.WithOrigins(corsOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
    else
    {
        p.WithOrigins("https://localhost:5007").AllowAnyHeader().AllowAnyMethod();
    }
}));

// ----- Kestrel & size limits -----
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 1_048_576; // 1 MiB
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 1_048_576);

// ----- ProblemDetails / exception -----
builder.Services.AddProblemDetails(opts =>
{
    opts.CustomizeProblemDetails = ctx =>
    {
        var cid = ctx.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
        if (!string.IsNullOrEmpty(cid))
            ctx.ProblemDetails.Extensions["traceId"] = cid;
    };
});

// ----- Health + OpenAPI -----
builder.Services.AddHealthChecks().AddDbContextCheck<ZeroTrustDbContext>("db");
builder.Services.AddOpenApi();

// ----- OpenTelemetry -----
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource("ZeroTrust.Authz").AddConsoleExporter())
        .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter("ZeroTrust.Authz").AddConsoleExporter());
}

var app = builder.Build();

// ----- Pipeline -----
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseCors("default");

app.UseAuthentication();
app.UseMiddleware<PartnerPostureMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapOpenApi();

app.MapAuthEndpoints();
app.MapCustomerEndpoints();
app.MapPartnerEndpoints();
app.MapAdminEndpoints();

app.MapGet("/", () => Results.Ok(new
{
    service = "Northstar Financial Services (fictional) — Zero-Trust API Platform",
    version = "1.0.0",
    surfaces = new[]
    {
        "/api/v1/customer/*",
        "/api/v1/partner/*",
        "/api/v1/admin/*"
    }
})).AllowAnonymous();

// ----- Startup: apply Api Key toggle then seed -----
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<ZeroTrustDbContext>();
    if (!app.Environment.IsEnvironment("Testing"))
    {
        var clock = sp.GetRequiredService<IClock>();
        await Seeder.SeedAsync(db, clock, CancellationToken.None);
    }
}

app.Run();

// Path -> expected JWT audience mapping (used by the JwtBearer OnMessageReceived hook).
static string ResolveAudienceForPath(PathString path)
{
    if (path.StartsWithSegments("/api/v1/partner")) return Audience.Partner;
    if (path.StartsWithSegments("/api/v1/admin")) return Audience.Admin;
    if (path.StartsWithSegments("/api/v1/customer")) return Audience.Customer;
    return Audience.Customer;
}

public partial class Program;
