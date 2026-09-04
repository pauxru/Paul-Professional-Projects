using Microsoft.AspNetCore.Http;

namespace EnterpriseSearch.Api;

internal static class ApiProblems
{
    public static IResult Validation(HttpContext context, string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid request",
        detail: detail,
        extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier });

    public static IResult NotFound(HttpContext context, string detail) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found",
        detail: detail,
        extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier });
}
