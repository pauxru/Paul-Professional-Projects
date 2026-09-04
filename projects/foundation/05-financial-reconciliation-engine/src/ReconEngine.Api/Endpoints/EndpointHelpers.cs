using System.Security.Claims;
using ReconEngine.Application.Common;

namespace ReconEngine.Api.Endpoints;

public static class EndpointHelpers
{
    /// <summary>The acting user's subject, used for audit trails on workflow actions.</summary>
    public static string CurrentUser(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? user.FindFirstValue("sub")
           ?? user.Identity?.Name
           ?? "anonymous";

    public static (int Page, int PageSize) NormalizePaging(int? page, int? pageSize)
    {
        var p = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null ? 50 : Math.Clamp(pageSize.Value, 1, PageRequest.MaxPageSize);
        return (p, size);
    }
}
