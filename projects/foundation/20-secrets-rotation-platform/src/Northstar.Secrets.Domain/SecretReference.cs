namespace Northstar.Secrets.Domain;

public readonly record struct SecretReference(string Name, int Version)
{
    public static SecretReference Create(string name, int version)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        return new SecretReference(SecretRecord.NormalizeName(name), version);
    }

    public static SecretReference Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("@secret:", StringComparison.Ordinal))
        {
            throw new FormatException("A secret reference must begin with '@secret:'.");
        }

        var separator = value.LastIndexOf("#v", StringComparison.Ordinal);
        if (separator < 8 || !int.TryParse(value[(separator + 2)..], out var version))
        {
            throw new FormatException("A secret reference must end with a numeric '#vN' version.");
        }

        return Create(value[8..separator], version);
    }

    public override string ToString() => $"@secret:{Name}#v{Version}";
}
