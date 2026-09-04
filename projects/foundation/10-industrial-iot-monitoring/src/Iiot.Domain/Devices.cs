namespace Iiot.Domain;

public sealed record DeviceRegistration(
    string DeviceId,
    string DeviceType,
    string Plant,
    string Line,
    string Asset,
    string FirmwareVersion,
    string EnrollmentToken,
    string? CertificateThumbprint = null);

public sealed record DeviceDescriptor(
    string DeviceId,
    string DeviceType,
    string Plant,
    string Line,
    string Asset,
    string FirmwareVersion,
    DeviceStatus Status,
    string? CertificateThumbprint,
    bool IsRevoked,
    DateTimeOffset CreatedAt);

public sealed record DeviceCredential(
    string DeviceId,
    string KeyHash,
    string EnrollmentTokenHash,
    string? CertificateThumbprint,
    bool IsRevoked);

public sealed class DeviceTwin
{
    private readonly Dictionary<string, string> _desired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reported = new(StringComparer.Ordinal);

    public int Version { get; private set; } = 1;
    public IReadOnlyDictionary<string, string> Desired => _desired;
    public IReadOnlyDictionary<string, string> Reported => _reported;

    public DeviceTwinSnapshot Snapshot() =>
        new(Version, new Dictionary<string, string>(_desired), new Dictionary<string, string>(_reported));

    public void PatchDesired(IReadOnlyDictionary<string, string?> patch, int expectedVersion)
    {
        EnsureVersion(expectedVersion);
        ApplyPatch(_desired, patch);
        Version++;
    }

    public void PatchReported(IReadOnlyDictionary<string, string?> patch, int expectedVersion)
    {
        EnsureVersion(expectedVersion);
        ApplyPatch(_reported, patch);
        Version++;
    }

    public static DeviceTwin FromSnapshot(DeviceTwinSnapshot snapshot)
    {
        var twin = new DeviceTwin { Version = snapshot.Version };
        foreach (var item in snapshot.Desired)
        {
            twin._desired[item.Key] = item.Value;
        }

        foreach (var item in snapshot.Reported)
        {
            twin._reported[item.Key] = item.Value;
        }

        return twin;
    }

    private void EnsureVersion(int expectedVersion)
    {
        if (expectedVersion != Version)
        {
            throw new DomainRuleViolation($"Twin version conflict. Expected {expectedVersion}, current version is {Version}.");
        }
    }

    private static void ApplyPatch(Dictionary<string, string> properties, IReadOnlyDictionary<string, string?> patch)
    {
        if (patch.Count == 0 || patch.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainRuleViolation("Twin patch must contain named properties.");
        }

        foreach (var (key, value) in patch)
        {
            if (value is null)
            {
                properties.Remove(key);
            }
            else
            {
                properties[key] = value;
            }
        }
    }
}

public sealed record DeviceTwinSnapshot(
    int Version,
    IReadOnlyDictionary<string, string> Desired,
    IReadOnlyDictionary<string, string> Reported);
