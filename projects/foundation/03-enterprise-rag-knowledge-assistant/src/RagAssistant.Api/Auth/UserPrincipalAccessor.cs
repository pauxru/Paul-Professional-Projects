using System.Security.Claims;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Api.Auth;

public interface IUserPrincipalAccessor
{
    UserPrincipal Get(HttpContext context);
}

public sealed class ClaimsUserPrincipalAccessor : IUserPrincipalAccessor
{
    public UserPrincipal Get(HttpContext context)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return UserPrincipal.Anonymous;
        }

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "anonymous";

        var roles = user.FindAll(ClaimTypes.Role)
            .Concat(user.FindAll("role"))
            .Concat(user.FindAll("roles"))
            .Select(c => c.Value)
            .SelectMany(v => v.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var departments = user.FindAll("department")
            .Concat(user.FindAll("dept"))
            .Concat(user.FindAll("departments"))
            .Select(c => c.Value)
            .SelectMany(v => v.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var classificationStr = user.FindFirst("classification")?.Value ?? "Internal";
        if (!Enum.TryParse<Classification>(classificationStr, ignoreCase: true, out var classification))
        {
            classification = Classification.Internal;
        }

        return new UserPrincipal(userId, roles, departments, classification);
    }
}
