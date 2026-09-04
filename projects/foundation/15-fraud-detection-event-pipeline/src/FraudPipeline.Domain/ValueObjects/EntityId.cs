namespace FraudPipeline.Domain.ValueObjects;

public enum EntityType
{
    Card = 1,
    Customer = 2,
    Device = 3,
    Ip = 4,
    Merchant = 5
}

/// <summary>
/// Strong-typed identifier for an entity in the pipeline.
/// </summary>
public readonly record struct EntityId(EntityType Type, string Value)
{
    public static EntityId Of(EntityType type, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Entity id value is required.", nameof(value));
        if (value.Length > 128) throw new ArgumentException("Entity id value too long.", nameof(value));
        return new EntityId(type, value.Trim());
    }

    public string Composite => $"{Type}:{Value}";
    public override string ToString() => Composite;
}
