using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Api.Security;

public static class StorefrontScopes
{
    public const string CatalogueRead = "storefront.catalogue.read";
    public const string OrdersWrite = "storefront.orders.write";
}

public sealed class JwtTokenIssuer(
    IOptions<SecurityOptions> options,
    IClock clock) : ITokenIssuer
{
    public string Issue(string subject, IReadOnlyCollection<string> scopes, TimeSpan lifetime)
    {
        var settings = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim("scope", string.Join(' ', scopes)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ],
            notBefore: clock.UtcNow.UtcDateTime,
            expires: clock.UtcNow.Add(lifetime).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class StorefrontAuthentication
{
    public static IServiceCollection AddStorefrontAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>()
            ?? new SecurityOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(security.Authority))
                {
                    options.Authority = security.Authority;
                    options.Audience = security.Audience;
                    options.RequireHttpsMetadata = true;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidAudience = security.Audience,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(10),
                        NameClaimType = JwtRegisteredClaimNames.Sub
                    };
                }
                else
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = security.Issuer,
                        ValidateAudience = true,
                        ValidAudience = security.Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(security.SigningKey)),
                        ClockSkew = TimeSpan.FromSeconds(10),
                        NameClaimType = JwtRegisteredClaimNames.Sub
                    };
                }
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "CatalogueRead",
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => HasScope(context.User, StorefrontScopes.CatalogueRead)));
            options.AddPolicy(
                "OrdersWrite",
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => HasScope(context.User, StorefrontScopes.OrdersWrite)));
        });
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        return services;
    }

    private static bool HasScope(ClaimsPrincipal principal, string requiredScope)
    {
        return principal.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);
    }
}
