using System.Security.Claims;
using Idp.Api.Observability;

namespace Idp.Api.Endpoints;

/// <summary>Shared helpers for reading request identity/correlation and shaping ProblemDetails.</summary>
public static class EndpointHelpers
{
    public static string GetActor(this HttpContext http) =>
        http.User.FindFirstValue("sub")
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "api";

    public static string GetCorrelationId(this HttpContext http) =>
        http.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var value)
        && value is string s && !string.IsNullOrEmpty(s)
            ? s
            : http.TraceIdentifier;

    public static IResult Problem(string detail, int statusCode, string title) =>
        Results.Problem(detail: detail, statusCode: statusCode, title: title);
}
