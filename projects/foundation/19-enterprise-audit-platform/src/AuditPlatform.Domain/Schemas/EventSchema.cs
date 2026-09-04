namespace AuditPlatform.Domain.Schemas;

/// <summary>
/// Persistent record of a registered event-type schema. Each event type may have multiple
/// versions; a new version is only accepted if it is backwards-compatible with the previous
/// one (added optional fields OK; removing or retyping fields rejected).
/// </summary>
public sealed class EventSchema
{
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string SchemaJson { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; private set; }

    private EventSchema() { }

    public static EventSchema Create(Guid id, string eventType, int version, string schemaJson, string description, DateTimeOffset when)
        => new() { Id = id, EventType = eventType, Version = version, SchemaJson = schemaJson, Description = description, RegisteredAt = when };
}

public enum SchemaFieldKind { String, Integer, Number, Boolean, Object, Array, Any }

public sealed record SchemaField(string Name, SchemaFieldKind Kind, bool Required);

/// <summary>
/// Minimal, hand-rolled schema representation. We only need enough expressiveness to describe
/// audit payloads: named fields with kinds and required flags. Reaching for JSON Schema here
/// would introduce a heavier dependency without changing the compatibility story.
/// </summary>
public sealed class SchemaDefinition
{
    public IReadOnlyList<SchemaField> Fields { get; }

    public SchemaDefinition(IEnumerable<SchemaField> fields) => Fields = fields.ToList();

    public static SchemaDefinition Parse(string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json).RootElement;
        if (!doc.TryGetProperty("fields", out var fieldsArr) || fieldsArr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new ArgumentException("Schema JSON must contain a 'fields' array.", nameof(json));

        var fields = new List<SchemaField>();
        foreach (var f in fieldsArr.EnumerateArray())
        {
            var name = f.GetProperty("name").GetString() ?? throw new ArgumentException("Schema field must have 'name'.");
            var kindStr = f.GetProperty("kind").GetString() ?? "any";
            var required = f.TryGetProperty("required", out var r) && r.GetBoolean();
            var kind = Enum.TryParse<SchemaFieldKind>(kindStr, ignoreCase: true, out var k) ? k : SchemaFieldKind.Any;
            fields.Add(new SchemaField(name, kind, required));
        }
        return new SchemaDefinition(fields);
    }

    public IEnumerable<string> Validate(System.Text.Json.JsonElement payload)
    {
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            yield return "payload must be a JSON object";
            yield break;
        }
        foreach (var field in Fields)
        {
            if (!payload.TryGetProperty(field.Name, out var value))
            {
                if (field.Required) yield return $"required field '{field.Name}' is missing";
                continue;
            }
            var ok = field.Kind switch
            {
                SchemaFieldKind.String => value.ValueKind == System.Text.Json.JsonValueKind.String,
                SchemaFieldKind.Integer => value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out _),
                SchemaFieldKind.Number => value.ValueKind == System.Text.Json.JsonValueKind.Number,
                SchemaFieldKind.Boolean => value.ValueKind == System.Text.Json.JsonValueKind.True || value.ValueKind == System.Text.Json.JsonValueKind.False,
                SchemaFieldKind.Object => value.ValueKind == System.Text.Json.JsonValueKind.Object,
                SchemaFieldKind.Array => value.ValueKind == System.Text.Json.JsonValueKind.Array,
                SchemaFieldKind.Any => true,
                _ => true
            };
            if (!ok) yield return $"field '{field.Name}' should be {field.Kind} but was {value.ValueKind}";
        }
    }

    public static (bool IsCompatible, IReadOnlyList<string> Breakages) CheckBackwardsCompatibility(SchemaDefinition previous, SchemaDefinition next)
    {
        var breakages = new List<string>();
        var nextByName = next.Fields.ToDictionary(f => f.Name);
        foreach (var prev in previous.Fields)
        {
            if (!nextByName.TryGetValue(prev.Name, out var updated))
            {
                breakages.Add($"field '{prev.Name}' was removed");
                continue;
            }
            if (updated.Kind != prev.Kind)
                breakages.Add($"field '{prev.Name}' changed type from {prev.Kind} to {updated.Kind}");
            if (updated.Required && !prev.Required)
                breakages.Add($"field '{prev.Name}' became required (previously optional)");
        }
        foreach (var added in next.Fields)
        {
            if (previous.Fields.All(p => p.Name != added.Name) && added.Required)
                breakages.Add($"new field '{added.Name}' is required — must be optional to preserve compatibility");
        }
        return (breakages.Count == 0, breakages);
    }
}
