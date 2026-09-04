using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics;

namespace Lab.SampleApp.Middleware;

public static class StatusCodeProblemDetails
{
    public static async Task WriteAsync(StatusCodeContext statusCodeContext)
    {
        var response = statusCodeContext.HttpContext.Response;
        if (response.StatusCode < StatusCodes.Status400BadRequest || response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = response.StatusCode,
            Title = $"HTTP {response.StatusCode}",
            Type = $"https://httpstatuses.com/{response.StatusCode}",
            Instance = statusCodeContext.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = statusCodeContext.HttpContext.TraceIdentifier;
        response.ContentType = "application/problem+json";
        await response.WriteAsJsonAsync(problem);
    }
}
