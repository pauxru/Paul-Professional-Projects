using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public sealed record ContractViolation(string Path, string Message);

public sealed class PayloadContractValidator
{
    public IReadOnlyList<ContractViolation> Validate(JsonNode? payload, JsonContract contract)
    {
        var violations = new List<ContractViolation>();
        foreach (var field in contract.Fields)
        {
            var value = JsonPath.Get(payload, field.Path);
            if (value is null)
            {
                if (field.Required)
                {
                    violations.Add(new ContractViolation(field.Path, "Required field is missing."));
                }
                continue;
            }

            var actual = GetType(value);
            if (actual != field.Type && !(field.Type == ContractValueType.Number && actual == ContractValueType.Integer))
            {
                violations.Add(new ContractViolation(field.Path, $"Expected {field.Type} but received {actual}."));
            }
        }

        return violations;
    }

    internal static ContractValueType GetType(JsonNode node)
    {
        if (node is JsonArray)
        {
            return ContractValueType.Array;
        }
        if (node is JsonObject)
        {
            return ContractValueType.Object;
        }
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out _))
            {
                return ContractValueType.Boolean;
            }
            if (value.TryGetValue<long>(out _))
            {
                return ContractValueType.Integer;
            }
            if (value.TryGetValue<decimal>(out _))
            {
                return ContractValueType.Number;
            }
            if (value.TryGetValue<string>(out _))
            {
                return ContractValueType.String;
            }
        }

        return ContractValueType.Null;
    }
}

public sealed record DriftChange(string Path, string Change, ContractValueType? Expected, ContractValueType? Actual);

public sealed record SchemaDriftReport(bool HasDrift, IReadOnlyList<DriftChange> Changes);

public sealed class SchemaDriftDetector
{
    public SchemaDriftReport Detect(JsonNode? payload, JsonContract contract)
    {
        var actual = Flatten(payload);
        var expected = contract.Fields.ToDictionary(x => x.Path, x => x, StringComparer.OrdinalIgnoreCase);
        var changes = new List<DriftChange>();

        foreach (var field in expected.Values)
        {
            if (!actual.TryGetValue(field.Path, out var actualType))
            {
                if (field.Required)
                {
                    changes.Add(new DriftChange(field.Path, "missing", field.Type, null));
                }
            }
            else if (actualType != field.Type
                     && !(field.Type == ContractValueType.Number && actualType == ContractValueType.Integer))
            {
                changes.Add(new DriftChange(field.Path, "retyped", field.Type, actualType));
            }
        }

        if (!contract.AllowAdditionalFields)
        {
            changes.AddRange(actual
                .Where(x => !expected.ContainsKey(x.Key))
                .Select(x => new DriftChange(x.Key, "new", null, x.Value)));
        }

        return new SchemaDriftReport(changes.Count > 0, changes);
    }

    private static Dictionary<string, ContractValueType> Flatten(JsonNode? node)
    {
        var result = new Dictionary<string, ContractValueType>(StringComparer.OrdinalIgnoreCase);
        Walk(node, "$", result);
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, ContractValueType> result)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var childPath = $"{path}.{property.Key}";
                if (property.Value is JsonObject)
                {
                    Walk(property.Value, childPath, result);
                }
                else if (property.Value is JsonArray array)
                {
                    result[childPath] = ContractValueType.Array;
                    if (array.Count > 0 && array[0] is JsonObject)
                    {
                        Walk(array[0], $"{childPath}[0]", result);
                    }
                }
                else if (property.Value is not null)
                {
                    result[childPath] = PayloadContractValidator.GetType(property.Value);
                }
            }
        }
    }
}

public sealed record WebhookVerificationResult(bool IsValid, string? Error);

public sealed class WebhookVerifier(IClock clock, IWebhookNonceStore nonceStore)
{
    public async Task<WebhookVerificationResult> VerifyAsync(
        ReadOnlyMemory<byte> body,
        string signature,
        string timestamp,
        string nonce,
        string secret,
        TimeSpan tolerance,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(timestamp, out var unixSeconds))
        {
            return new WebhookVerificationResult(false, "Invalid timestamp.");
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((clock.UtcNow - sentAt).Duration() > tolerance)
        {
            return new WebhookVerificationResult(false, "Timestamp is outside the replay window.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.{nonce}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsMemory(prefix.Length));
        var expected = Convert.ToHexString(hmac.ComputeHash(signed)).ToLowerInvariant();

        byte[] suppliedBytes;
        byte[] expectedBytes;
        try
        {
            suppliedBytes = Convert.FromHexString(signature);
            expectedBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return new WebhookVerificationResult(false, "Invalid signature encoding.");
        }

        if (suppliedBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        {
            return new WebhookVerificationResult(false, "Signature mismatch.");
        }

        if (string.IsNullOrWhiteSpace(nonce)
            || !await nonceStore.TryUseAsync(nonce, clock.UtcNow.Add(tolerance), cancellationToken))
        {
            return new WebhookVerificationResult(false, "Nonce has already been used.");
        }

        return new WebhookVerificationResult(true, null);
    }
}

public sealed class ConnectorUrlGuard(
    IReadOnlySet<string> allowedHosts,
    bool allowHttp = false,
    bool allowPrivateNetworks = false)
{
    public async Task ValidateAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException("Connector URL must use an allowed HTTP scheme.");
        }

        if (!allowedHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException($"Connector host '{uri.Host}' is not in the allow-list.");
        }

        if (allowPrivateNetworks)
        {
            return;
        }

        var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        if (addresses.Any(IsPrivateOrSpecial))
        {
            throw new InvalidOperationException("Connector URL resolves to a private or special-use network.");
        }
    }

    private static bool IsPrivateOrSpecial(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || bytes[0] == 0
                   || bytes[0] >= 224;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Loopback)
               || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}
