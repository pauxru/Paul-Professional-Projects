using System.IO;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Idempotency;
using Contoso.Payments.Infrastructure.Observability;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.Api.Middleware;

/// <summary>
/// Idempotency filter for POST endpoints that opt in.  On the first request for a given
/// (endpoint, key) tuple the response is captured and stored.  A subsequent request with the
/// same key and same body hash returns the stored response verbatim (200/201 preserved).  A
/// subsequent request with the same key but a different body returns 409.
///
/// This middleware runs late in the pipeline (after auth, after correlation-id) so we only pay
/// the cost for authenticated writes.
/// </summary>
public sealed class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";
    private static readonly HashSet<string> IdempotentEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST /api/v1/orders",
        "POST /api/v1/payments/authorize",
        "POST /api/v1/payments/capture",
        "POST /api/v1/payments/void",
        "POST /api/v1/refunds"
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _log;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, PaymentMetrics metrics)
    {
        var endpointKey = $"{ctx.Request.Method} {NormalisePath(ctx.Request.Path.Value ?? "")}";
        if (!IdempotentEndpoints.Contains(endpointKey))
        {
            await _next(ctx);
            return;
        }
        var key = ctx.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            ctx.Response.StatusCode = 400;
            await WriteProblemAsync(ctx, "idempotency.key_required",
                $"Header {HeaderName} is required on {endpointKey}.", 400);
            return;
        }

        ctx.Request.EnableBuffering();
        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(ms);
            bodyBytes = ms.ToArray();
        }
        ctx.Request.Body.Position = 0;

        var hash = Hashing.Sha256Hex(bodyBytes);

        var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(
            r => r.Key == key && r.Endpoint == endpointKey);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
            {
                _log.LogWarning("Idempotency conflict for key {Key}", key);
                ctx.Response.StatusCode = 409;
                await WriteProblemAsync(ctx, "idempotency.conflict",
                    "This idempotency key was previously used with a different request body.", 409);
                return;
            }
            metrics.IdempotencyReplays.Add(1);
            ctx.Response.StatusCode = existing.ResponseStatus;
            ctx.Response.ContentType = existing.ResponseContentType;
            ctx.Response.Headers["Idempotent-Replay"] = "true";
            await ctx.Response.WriteAsync(existing.ResponseBody);
            return;
        }

        var originalBody = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await _next(ctx);
            buffer.Position = 0;
            var responseText = Encoding.UTF8.GetString(buffer.ToArray());
            if (ctx.Response.StatusCode is >= 200 and < 300)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Key = key,
                    Endpoint = endpointKey,
                    RequestHash = hash,
                    ResponseStatus = ctx.Response.StatusCode,
                    ResponseBody = responseText,
                    ResponseContentType = ctx.Response.ContentType ?? "application/json",
                    CorrelationId = ctx.CorrelationId(),
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    // Concurrent identical request beat us; benign.
                }
            }
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
        }
        finally
        {
            ctx.Response.Body = originalBody;
        }
    }

    private static string NormalisePath(string path)
    {
        // Collapse trailing slash; do not attempt to strip route parameters here because the
        // set of idempotent endpoints is intentionally the "collection POST" pattern.
        var p = path.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    private static async Task WriteProblemAsync(HttpContext ctx, string code, string detail, int status)
    {
        ctx.Response.ContentType = "application/problem+json";
        var problem = new
        {
            type = $"https://contoso-payments.local/errors/{code}",
            title = code,
            status,
            detail,
            traceId = ctx.CorrelationId()
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
