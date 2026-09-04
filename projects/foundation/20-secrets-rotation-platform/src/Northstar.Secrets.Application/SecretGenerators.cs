using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Application;

public sealed class SecretGeneratorRegistry(IEnumerable<ISecretGenerator> generators)
    : ISecretGeneratorRegistry
{
    private readonly IReadOnlyList<ISecretGenerator> _generators = generators.ToArray();

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        var generator = _generators.SingleOrDefault(x => x.SupportedTypes.Contains(type))
            ?? throw new ApplicationValidationException($"No generator is registered for {type}.");
        return generator.Generate(type, now);
    }
}

public sealed class PasswordSecretGenerator : ISecretGenerator
{
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*()-_=+";
    private const int Length = 32;

    public IReadOnlySet<SecretType> SupportedTypes { get; } =
        new HashSet<SecretType> { SecretType.DatabasePassword };

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        var all = Lower + Upper + Digits + Symbols;
        var characters = new List<char>
        {
            RandomCharacter(Lower),
            RandomCharacter(Upper),
            RandomCharacter(Digits),
            RandomCharacter(Symbols)
        };

        while (characters.Count < Length)
        {
            characters.Add(RandomCharacter(all));
        }

        for (var index = characters.Count - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }

        var entropy = Length * Math.Log2(all.Length);
        return new GeneratedSecret(
            new string(characters.ToArray()),
            "password",
            new Dictionary<string, string>
            {
                ["length"] = Length.ToString(),
                ["estimatedEntropyBits"] = entropy.ToString("F1")
            });
    }

    private static char RandomCharacter(string alphabet) =>
        alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}

public sealed class ApiKeySecretGenerator : ISecretGenerator
{
    public IReadOnlySet<SecretType> SupportedTypes { get; } =
        new HashSet<SecretType> { SecretType.ApiKey, SecretType.WebhookSigningSecret };

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        var bytes = RandomNumberGenerator.GetBytes(type == SecretType.ApiKey ? 32 : 48);
        var encoded = Base64Url(bytes);
        var prefix = type == SecretType.ApiKey ? "nsk_demo_" : "whsec_demo_";
        return new GeneratedSecret(
            prefix + encoded,
            type == SecretType.ApiKey ? "api-key" : "hmac-key",
            new Dictionary<string, string>
            {
                ["entropyBits"] = (bytes.Length * 8).ToString(),
                ["encoding"] = "base64url"
            });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class EncryptionKeySecretGenerator : ISecretGenerator
{
    public IReadOnlySet<SecretType> SupportedTypes { get; } =
        new HashSet<SecretType> { SecretType.EncryptionKey };

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return new GeneratedSecret(
            Convert.ToBase64String(key),
            "aes-256-key",
            new Dictionary<string, string> { ["algorithm"] = "AES-256", ["entropyBits"] = "256" });
    }
}

public sealed class ConnectionStringSecretGenerator : ISecretGenerator
{
    public IReadOnlySet<SecretType> SupportedTypes { get; } =
        new HashSet<SecretType> { SecretType.ConnectionString };

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var value =
            $"Server=synthetic-db.invalid;Database=demo;User Id=demo_app;Password={password};Encrypt=true";
        return new GeneratedSecret(
            value,
            "connection-string",
            new Dictionary<string, string> { ["provider"] = "synthetic-sql", ["passwordBits"] = "192" });
    }
}

public sealed class KeyPairSecretGenerator : ISecretGenerator
{
    public IReadOnlySet<SecretType> SupportedTypes { get; } =
        new HashSet<SecretType> { SecretType.ServiceAccountKey, SecretType.SigningKey };

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        if (type == SecretType.ServiceAccountKey)
        {
            using var rsa = RSA.Create(2048);
            var payload = JsonSerializer.Serialize(new
            {
                algorithm = "RSA-2048",
                privateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
                publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(),
                keyId = Guid.NewGuid().ToString("N")
            });
            return new GeneratedSecret(
                payload,
                "rsa-keypair-json",
                new Dictionary<string, string> { ["algorithm"] = "RSA-2048" });
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signingPayload = JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA-P256",
            privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem(),
            publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem(),
            keyId = Guid.NewGuid().ToString("N")
        });
        return new GeneratedSecret(
            signingPayload,
            "ecdsa-keypair-json",
            new Dictionary<string, string> { ["algorithm"] = "ECDSA-P256" });
    }
}

public sealed class CertificateSecretGenerator : ISecretGenerator
{
    public IReadOnlySet<SecretType> SupportedTypes { get; } =
        new HashSet<SecretType> { SecretType.Certificate };

    public GeneratedSecret Generate(SecretType type, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Northstar Synthetic Demo",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = now.AddMinutes(-5);
        var notAfter = now.AddDays(90);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var exportPassword = "runtime-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var payload = JsonSerializer.Serialize(new
        {
            pfxBase64 = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, exportPassword)),
            password = exportPassword,
            thumbprint = certificate.Thumbprint,
            notBefore = notBefore,
            notAfter = notAfter
        });

        return new GeneratedSecret(
            payload,
            "pkcs12-json",
            new Dictionary<string, string>
            {
                ["subject"] = certificate.Subject,
                ["validityDays"] = "90",
                ["algorithm"] = "RSA-2048/SHA-256"
            });
    }
}
