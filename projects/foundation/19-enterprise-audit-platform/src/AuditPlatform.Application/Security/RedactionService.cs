using AuditPlatform.Application.Events;
using AuditPlatform.Domain.Events;

namespace AuditPlatform.Application.Security;

public sealed record ReaderContext(
    string ActorId,
    string ActorDisplayName,
    ActorType ActorType,
    IReadOnlyList<string> Scopes,
    string TenantId,
    string CorrelationId,
    string SourceIp,
    string UserAgent,
    ClearanceLevel Clearance,
    bool IsMetaAuditor);

public enum ClearanceLevel
{
    Standard = 0,
    Elevated = 1,
    Investigator = 2
}

/// <summary>
/// Field-level redaction. A reader without <see cref="ClearanceLevel.Investigator"/> clearance
/// receives events whose before/after payloads have been erased from the DTO — but the *hash* of
/// the original before/after is preserved, so integrity of the un-redacted event can still be
/// demonstrated to an auditor with the correct clearance.
/// </summary>
public sealed class RedactionService
{
    public EventDto Redact(EventDto dto, ReaderContext reader)
    {
        if (reader.Clearance == ClearanceLevel.Investigator) return dto;
        if (dto.Payload.ValueKind != System.Text.Json.JsonValueKind.Object) return dto;

        var payload = System.Text.Json.JsonDocument.Parse(dto.Payload.GetRawText()).RootElement;
        // Strip the "before" and "after" fields when clearance is insufficient.
        var raw = payload.GetRawText();
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var rewritten = RewriteWithoutBeforeAfter(doc.RootElement);
        var newElement = System.Text.Json.JsonDocument.Parse(rewritten).RootElement;
        return dto with { Payload = newElement };
    }

    private static string RewriteWithoutBeforeAfter(System.Text.Json.JsonElement root)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name is "before" or "after")
                {
                    writer.WritePropertyName(p.Name);
                    writer.WriteStringValue("REDACTED");
                    continue;
                }
                p.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
