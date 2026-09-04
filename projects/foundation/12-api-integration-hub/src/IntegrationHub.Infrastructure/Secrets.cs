using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationHub.Application;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Infrastructure;

public sealed class SecretRedactor : ISecretRedactor
{
    private readonly object _gate = new();
    private readonly HashSet<string> _secretValues = new(StringComparer.Ordinal);
    private static readonly string[] SensitiveKeys =
    [
        "secret", "password", "token", "apikey", "api_key", "authorization",
        "email", "phone", "firstname", "lastname", "fullName", "address"
    ];

    public void Register(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            lock (_gate)
            {
                _secretValues.Add(value);
            }
        }
    }

    public string Redact(string value)
    {
        var redacted = value;
        lock (_gate)
        {
            foreach (var secret in _secretValues.OrderByDescending(x => x.Length))
            {
                redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        return redacted;
    }

    public JsonNode? Redact(JsonNode? value)
    {
        var clone = value?.DeepClone();
        if (clone is JsonValue rootValue && rootValue.TryGetValue<string>(out var rootText))
        {
            return JsonValue.Create(Redact(rootText));
        }
        RedactNode(clone);
        return clone;
    }

    private void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (SensitiveKeys.Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    obj[key] = "[REDACTED]";
                }
                else
                {
                    RedactNode(obj[key]);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                RedactNode(child);
            }
        }
        else if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            var redacted = Redact(text);
            if (!string.Equals(text, redacted, StringComparison.Ordinal))
            {
                node!.ReplaceWith(redacted);
            }
        }
    }
}

public sealed class EncryptedFileSecretStore(
    IOptions<SecretsOptions> options,
    IClock clock,
    SecretRedactor redactor) : ISecretStore
{
    private readonly SecretsOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default) =>
        RotateInternalAsync(name, value, false, cancellationToken);

    public async Task<string> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadAsync(cancellationToken);
            if (!data.TryGetValue(name, out var secret) || secret.Versions.Count == 0)
            {
                throw new KeyNotFoundException($"Secret '{name}' was not found.");
            }
            var value = secret.Versions.Single(x => x.Version == secret.CurrentVersion).Value;
            redactor.Register(value);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> RotateAsync(string name, string value, CancellationToken cancellationToken = default) =>
        RotateInternalAsync(name, value, true, cancellationToken);

    public async Task<IReadOnlyCollection<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadAsync(cancellationToken);
            return data.Select(x => new SecretMetadata(x.Key, x.Value.CurrentVersion, x.Value.UpdatedAt)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> RotateInternalAsync(
        string name,
        string value,
        bool requireExisting,
        CancellationToken cancellationToken)
    {
        Validate(name, value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadAsync(cancellationToken);
            if (requireExisting && !data.ContainsKey(name))
            {
                throw new KeyNotFoundException($"Secret '{name}' was not found.");
            }

            if (!data.TryGetValue(name, out var entry))
            {
                entry = new StoredSecret();
                data[name] = entry;
            }

            var version = entry.Versions.Count == 0 ? 1 : entry.Versions.Max(x => x.Version) + 1;
            entry.CurrentVersion = version;
            entry.UpdatedAt = clock.UtcNow;
            entry.Versions.Add(new StoredSecretVersion { Version = version, Value = value, CreatedAt = clock.UtcNow });
            await WriteAsync(data, cancellationToken);
            redactor.Register(value);
            return version;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, StoredSecret>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.FilePath))
        {
            return new Dictionary<string, StoredSecret>(StringComparer.OrdinalIgnoreCase);
        }

        var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(
                           await File.ReadAllTextAsync(_options.FilePath, cancellationToken))
                       ?? throw new CryptographicException("Invalid secret store envelope.");
        var key = GetKey();
        var plaintext = new byte[Convert.FromBase64String(envelope.Ciphertext).Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(
            Convert.FromBase64String(envelope.Nonce),
            Convert.FromBase64String(envelope.Ciphertext),
            Convert.FromBase64String(envelope.Tag),
            plaintext);
        return JsonSerializer.Deserialize<Dictionary<string, StoredSecret>>(plaintext)
               ?? new Dictionary<string, StoredSecret>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteAsync(Dictionary<string, StoredSecret> data, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(GetKey(), 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var envelope = new EncryptedEnvelope(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
        var directory = Path.GetDirectoryName(Path.GetFullPath(_options.FilePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(_options.FilePath, JsonSerializer.Serialize(envelope), cancellationToken);
    }

    private byte[] GetKey()
    {
        var configured = Environment.GetEnvironmentVariable("INTEGRATIONHUB_SECRETS_MASTERKEY")
                         ?? _options.MasterKey;
        var key = Convert.FromBase64String(configured);
        if (key.Length != 32)
        {
            throw new CryptographicException("Secret master key must be a base64 encoded 256-bit key.");
        }
        return key;
    }

    private static void Validate(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Secret name is invalid.", nameof(name));
        }
        if (string.IsNullOrEmpty(value) || value.Length is < 8 or > 64_000)
        {
            throw new ArgumentException("Secret value must contain 8 to 64,000 characters.", nameof(value));
        }
    }

    private sealed class StoredSecret
    {
        public int CurrentVersion { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StoredSecretVersion> Versions { get; set; } = [];
    }

    private sealed class StoredSecretVersion
    {
        public int Version { get; set; }
        public string Value { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed record EncryptedEnvelope(string Nonce, string Ciphertext, string Tag);
}

public sealed class SecretReferenceResolver(ISecretStore store)
{
    public async Task<string?> ResolveAsync(string? value, CancellationToken cancellationToken)
    {
        const string prefix = "@secret:";
        return value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? await store.GetAsync(value[prefix.Length..], cancellationToken)
            : value;
    }
}
