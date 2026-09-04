using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Northstar.Application.Abstractions;

namespace Northstar.Api.Endpoints;

public static class Audit
{
    public static Task WriteAsync(
        HttpContext context,
        IAuditWriter writer,
        IClock clock,
        string action,
        string resource,
        object? before,
        object? after,
        CancellationToken cancellationToken)
    {
        var actor = context.User.FindFirst("sub")?.Value ?? "anonymous";
        var correlationId = context.Response.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
        return writer.WriteAsync(
            new AuditEntry(
                Truncate(actor, 200) ?? "anonymous",
                action,
                Truncate(resource, 300) ?? "unknown",
                clock.UtcNow,
                Truncate(correlationId, 128) ?? context.TraceIdentifier,
                Truncate(context.Connection.RemoteIpAddress?.ToString(), 64),
                Truncate(context.Request.Headers.UserAgent.ToString(), 512),
                Hash(before),
                Hash(after)),
            cancellationToken);
    }

    private static string Hash(object? value)
    {
        var serialized = JsonSerializer.Serialize(value ?? new { empty = true });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
