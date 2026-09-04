using AuditPlatform.Application.Abstractions;
using AuditPlatform.Domain.Schemas;

namespace AuditPlatform.Application.Schemas;

public sealed class SchemaRegistryService
{
    private readonly ISchemaRegistry _registry;

    public SchemaRegistryService(ISchemaRegistry registry) => _registry = registry;

    public sealed record RegisterResult(bool Accepted, int Version, IReadOnlyList<string> Breakages);

    public async Task<RegisterResult> RegisterAsync(string eventType, string schemaJson, string description, CancellationToken ct)
    {
        var latest = await _registry.GetLatestAsync(eventType, ct);
        var candidate = SchemaDefinition.Parse(schemaJson);
        if (latest is not null)
        {
            var previous = SchemaDefinition.Parse(latest.SchemaJson);
            var (ok, breakages) = SchemaDefinition.CheckBackwardsCompatibility(previous, candidate);
            if (!ok) return new RegisterResult(false, latest.Version, breakages);
        }
        var registered = await _registry.RegisterAsync(eventType, schemaJson, description, ct);
        return new RegisterResult(true, registered.Version, Array.Empty<string>());
    }
}
