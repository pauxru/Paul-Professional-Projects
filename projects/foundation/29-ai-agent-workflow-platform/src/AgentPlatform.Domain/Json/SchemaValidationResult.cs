namespace AgentPlatform.Domain.Json;

/// <summary>A single schema validation failure, addressed by JSON pointer-ish path.</summary>
public sealed record SchemaError(string Path, string Message)
{
    public override string ToString() => $"{(string.IsNullOrEmpty(Path) ? "$" : Path)}: {Message}";
}

/// <summary>Outcome of validating (and optionally coercing) a JSON value against a schema.</summary>
public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<SchemaError> Errors)
{
    public static SchemaValidationResult Valid { get; } = new(true, Array.Empty<SchemaError>());

    public static SchemaValidationResult Invalid(params SchemaError[] errors) => new(false, errors);

    public string Summary => IsValid
        ? "valid"
        : string.Join("; ", Errors.Select(e => e.ToString()));
}
