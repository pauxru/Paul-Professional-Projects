using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JobScheduler.Api.Auth;

/// <summary>Registers JWT bearer authentication and the scope-based authorization policies.</summary>
public static class AuthSetup
{
    public static IServiceCollection AddSchedulerAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName));

        services.AddSingleton<TokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.PolicyRead, p => p.RequireAssertion(ctx =>
                HasScope(ctx, AuthConstants.ScopeRead, AuthConstants.ScopeTrigger, AuthConstants.ScopeManage, AuthConstants.ScopeAdmin)))
            .AddPolicy(AuthConstants.PolicyTrigger, p => p.RequireAssertion(ctx =>
                HasScope(ctx, AuthConstants.ScopeTrigger, AuthConstants.ScopeAdmin)))
            .AddPolicy(AuthConstants.PolicyManage, p => p.RequireAssertion(ctx =>
                HasScope(ctx, AuthConstants.ScopeManage, AuthConstants.ScopeAdmin)))
            .AddPolicy(AuthConstants.PolicyAdmin, p => p.RequireAssertion(ctx =>
                HasScope(ctx, AuthConstants.ScopeAdmin)));

        return services;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext ctx, params string[] accepted)
    {
        var held = ctx.User.FindAll(AuthConstants.ScopeClaim).Select(c => c.Value);
        var set = new HashSet<string>(held, StringComparer.Ordinal);
        return accepted.Any(set.Contains);
    }

    /// <summary>Fails fast if the insecure default signing key is used outside Development/Testing.</summary>
    public static void GuardSigningKey(this IServiceProvider services, IHostEnvironment environment)
    {
        var jwt = services.GetRequiredService<IOptions<JwtOptions>>().Value;
        if (jwt.IsDefaultKey && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "The default JWT signing key must not be used in Production. Set Jwt:SigningKey via configuration or environment.");
        }
    }
}
