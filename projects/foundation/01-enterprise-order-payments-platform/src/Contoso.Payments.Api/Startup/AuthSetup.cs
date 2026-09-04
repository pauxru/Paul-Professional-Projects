using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

using Contoso.Payments.Application.Common;

namespace Contoso.Payments.Api.Startup;

public static class AuthSetup
{
    public const string DefaultSigningKey = "dev-only-not-a-real-secret-change-me-0123456789ABCDEF";

    public static WebApplicationBuilder AddPaymentsAuth(this WebApplicationBuilder builder)
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? new JwtOptions { SigningKey = DefaultSigningKey };

        // Refuse to boot in Production with default key.
        if (builder.Environment.IsProduction() && string.Equals(jwt.SigningKey, DefaultSigningKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Refusing to start in Production with the default JWT signing key.  Set Jwt:SigningKey.");

        builder.Services.AddSingleton(jwt);
        builder.Services.AddSingleton<DevTokenIssuer>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("orders:write", p => p.RequireAuthenticatedUser().RequireClaim("scope", "orders:write"));
            o.AddPolicy("admin", p => p.RequireAuthenticatedUser().RequireClaim("scope", "admin"));
            o.AddPolicy("reconciliation:run", p => p.RequireAuthenticatedUser().RequireClaim("scope", "reconciliation:run"));
        });

        return builder;
    }
}
