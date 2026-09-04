using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Application;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.Api;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = string.IsNullOrWhiteSpace(incoming) || incoming.Length > 128
            ? Guid.NewGuid().ToString("N")
            : incoming.Trim();
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId
               }))
        {
            await next(context);
        }
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] =
                context.Request.Path.StartsWithSegments("/docs") ||
                context.Request.Path.StartsWithSegments("/swagger")
                    ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'"
                    : "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });
        await next(context);
    }
}

public sealed class IdempotencyMiddleware(
    RequestDelegate next,
    ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";

    public async Task InvokeAsync(
        HttpContext context,
        BillingDbContext db,
        IClock clock,
        IIdGenerator ids)
    {
        if (!RequiresIdempotency(context.Request))
        {
            await next(context);
            return;
        }

        var key = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Idempotency-Key is required",
                "State-changing API requests require an Idempotency-Key header.",
                new Dictionary<string, string[]>
                {
                    [HeaderName] = ["Provide a unique value of at most 128 characters."]
                });
            return;
        }

        var route = $"{context.Request.Method}:{context.Request.Path}";
        var requestHash = await HashRequestAsync(context.Request, context.RequestAborted);
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.Key == key && item.Route == route,
            context.RequestAborted);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "Idempotency conflict",
                    "The key was already used with a different request body.");
                return;
            }

            context.Response.StatusCode = existing.ResponseStatusCode;
            context.Response.ContentType = existing.ResponseContentType;
            context.Response.Headers["Idempotency-Replayed"] = "true";
            await context.Response.WriteAsync(existing.ResponseBody, context.RequestAborted);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            buffer.Position = 0;
            var responseBody = await new StreamReader(buffer, Encoding.UTF8, leaveOpen: true)
                .ReadToEndAsync(context.RequestAborted);
            if (context.Response.StatusCode < 500)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecordEntity
                {
                    Id = ids.NewGuid(),
                    Key = key.Trim(),
                    Route = route,
                    RequestHash = requestHash,
                    ResponseStatusCode = context.Response.StatusCode,
                    ResponseContentType = context.Response.ContentType ?? "application/json",
                    ResponseBody = responseBody,
                    CreatedAt = clock.UtcNow,
                    ExpiresAt = clock.UtcNow.AddHours(24)
                });
                try
                {
                    await db.SaveChangesAsync(context.RequestAborted);
                }
                catch (DbUpdateException exception)
                {
                    logger.LogInformation(
                        exception,
                        "Concurrent idempotency record insert for key {IdempotencyKey}",
                        key);
                }
            }

            buffer.Position = 0;
            context.Response.Body = originalBody;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool RequiresIdempotency(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api/v1") ||
            request.Method is "GET" or "HEAD" or "OPTIONS")
        {
            return false;
        }

        return !request.Path.StartsWithSegments("/api/v1/auth") &&
               !request.Path.StartsWithSegments("/api/v1/usage") &&
               !request.Path.StartsWithSegments("/api/v1/webhooks/payments");
    }

    private static async Task<string> HashRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(request.Body, cancellationToken);
        request.Body.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }
}
