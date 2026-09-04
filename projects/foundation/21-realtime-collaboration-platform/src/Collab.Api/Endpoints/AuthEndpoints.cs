using System.ComponentModel.DataAnnotations;
using Collab.Api.Auth;
using Collab.Application.Abstractions;
using Collab.Domain.Abstractions;
using Collab.Domain.Identity;

namespace Collab.Api.Endpoints;

/// <summary>
/// Demo authentication: exchange an email for a signed JWT, provisioning the user on first sight.
/// Password-less on purpose — identity is not the point of this project and this keeps the realtime
/// demo friction-free. Documented as demo-grade in the security review.
/// </summary>
public static class AuthEndpoints
{
    public sealed record TokenRequest([property: Required, EmailAddress] string Email, string? DisplayName);
    public sealed record TokenResponse(string Token, Guid UserId, string Email, string DisplayName, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/token", async (
            TokenRequest request,
            IUserRepository users,
            IUnitOfWork uow,
            JwtTokenService tokens,
            IClock clock,
            CancellationToken ct) =>
        {
            var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email is required."] });

            var user = await users.GetByEmailAsync(email, ct);
            if (user is null)
            {
                user = new User(string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName!.Trim(), email, clock);
                users.Add(user);
                await uow.SaveChangesAsync(ct);
            }

            var (token, expiresAt) = tokens.Create(user.Id, user.Email, user.DisplayName);
            return Results.Ok(new TokenResponse(token, user.Id, user.Email, user.DisplayName, expiresAt));
        })
        .AllowAnonymous()
        .WithName("IssueToken");

        return app;
    }
}
