using System.Security.Claims;
using Healthcare.Application.Abstractions;
using Healthcare.Domain.Common;

namespace Healthcare.Api.Auth;

public sealed class HttpContextCurrentUser : ICurrentUser
{
    public const string BreakGlassHeader = "X-Break-Glass";
    public const string BreakGlassJustificationHeader = "X-Break-Glass-Justification";
    public const string ClinicianIdClaim = "clinician_id";

    private readonly IHttpContextAccessor _accessor;
    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private HttpContext? Ctx => _accessor.HttpContext;

    public string UserId => Ctx?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? Ctx?.User?.FindFirstValue("sub") ?? "anonymous";

    public string DisplayName => Ctx?.User?.FindFirstValue("name")
        ?? Ctx?.User?.Identity?.Name ?? UserId;

    public IReadOnlyCollection<string> Roles => Ctx?.User?.Claims
        .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
        .Select(c => c.Value).ToArray() ?? Array.Empty<string>();

    public Guid? ClinicianId
    {
        get
        {
            var raw = Ctx?.User?.FindFirstValue(ClinicianIdClaim);
            return Guid.TryParse(raw, out var g) ? g : null;
        }
    }

    public string CorrelationId => Ctx?.Items[Middleware.CorrelationIdMiddleware.HeaderName] as string
        ?? Guid.NewGuid().ToString("N");

    public string? SourceIp => Ctx?.Connection?.RemoteIpAddress?.ToString();
    public string? UserAgent => Ctx?.Request?.Headers["User-Agent"].ToString();

    public bool BreakGlassActivated =>
        Ctx?.Request?.Headers[BreakGlassHeader].ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    public string? BreakGlassJustification =>
        Ctx?.Request?.Headers[BreakGlassJustificationHeader].ToString();

    public bool HasRole(string role) => Roles.Contains(role);

    public bool IsAuthenticated => Ctx?.User?.Identity?.IsAuthenticated ?? false;
}
