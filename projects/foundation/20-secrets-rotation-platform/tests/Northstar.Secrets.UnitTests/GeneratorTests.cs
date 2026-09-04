using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.UnitTests;

public sealed class GeneratorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PasswordGenerator_EnforcesLengthAndCharacterClasses()
    {
        var generated = new PasswordSecretGenerator().Generate(SecretType.DatabasePassword, Now);

        Assert.Equal(32, generated.Value.Length);
        Assert.Contains(generated.Value, char.IsLower);
        Assert.Contains(generated.Value, char.IsUpper);
        Assert.Contains(generated.Value, char.IsDigit);
        Assert.Contains(generated.Value, character => "!@#$%^&*()-_=+".Contains(character));
    }

    [Fact]
    public void PasswordGenerator_ReportsHighEstimatedEntropy()
    {
        var generated = new PasswordSecretGenerator().Generate(SecretType.DatabasePassword, Now);

        Assert.True(double.Parse(generated.Metadata["estimatedEntropyBits"]) > 180);
    }

    [Fact]
    public void ApiKeyGenerator_ProducesPrefixed256BitKey()
    {
        var generated = new ApiKeySecretGenerator().Generate(SecretType.ApiKey, Now);

        Assert.StartsWith("nsk_demo_", generated.Value);
        Assert.Equal("256", generated.Metadata["entropyBits"]);
    }

    [Fact]
    public void WebhookGenerator_ProducesDistinctHmacMaterial()
    {
        var generator = new ApiKeySecretGenerator();
        var first = generator.Generate(SecretType.WebhookSigningSecret, Now);
        var second = generator.Generate(SecretType.WebhookSigningSecret, Now);

        Assert.StartsWith("whsec_demo_", first.Value);
        Assert.NotEqual(first.Value, second.Value);
        Assert.Equal("384", first.Metadata["entropyBits"]);
    }

    [Fact]
    public void EncryptionKeyGenerator_ProducesAes256Key()
    {
        var generated = new EncryptionKeySecretGenerator().Generate(SecretType.EncryptionKey, Now);

        Assert.Equal(32, Convert.FromBase64String(generated.Value).Length);
        Assert.Equal("AES-256", generated.Metadata["algorithm"]);
    }

    [Fact]
    public void ConnectionStringGenerator_ProducesEncryptedTransportSettingAndRandomPassword()
    {
        var generated = new ConnectionStringSecretGenerator().Generate(
            SecretType.ConnectionString, Now);

        Assert.Contains("Server=synthetic-db.invalid", generated.Value);
        Assert.Contains("Encrypt=true", generated.Value);
        Assert.Contains("Password=", generated.Value);
    }

    [Fact]
    public void ServiceAccountGenerator_ProducesUsableRsaKeypair()
    {
        var generated = new KeyPairSecretGenerator().Generate(SecretType.ServiceAccountKey, Now);
        using var document = JsonDocument.Parse(generated.Value);
        var privatePem = document.RootElement.GetProperty("privateKeyPem").GetString()!;
        var publicPem = document.RootElement.GetProperty("publicKeyPem").GetString()!;
        using var privateKey = RSA.Create();
        using var publicKey = RSA.Create();
        privateKey.ImportFromPem(privatePem);
        publicKey.ImportFromPem(publicPem);
        var data = Encoding.UTF8.GetBytes("synthetic-message");
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(publicKey.VerifyData(
            data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void SigningKeyGenerator_ProducesUsableEcdsaKeypair()
    {
        var generated = new KeyPairSecretGenerator().Generate(SecretType.SigningKey, Now);
        using var document = JsonDocument.Parse(generated.Value);
        var privatePem = document.RootElement.GetProperty("privateKeyPem").GetString()!;
        var publicPem = document.RootElement.GetProperty("publicKeyPem").GetString()!;
        using var privateKey = ECDsa.Create();
        using var publicKey = ECDsa.Create();
        privateKey.ImportFromPem(privatePem);
        publicKey.ImportFromPem(publicPem);
        var data = Encoding.UTF8.GetBytes("synthetic-message");
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256);

        Assert.True(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void CertificateGenerator_ProducesSelfSignedCertificateWithExpectedValidity()
    {
        var generated = new CertificateSecretGenerator().Generate(SecretType.Certificate, Now);
        using var document = JsonDocument.Parse(generated.Value);
        var pfx = Convert.FromBase64String(
            document.RootElement.GetProperty("pfxBase64").GetString()!);
        var password = document.RootElement.GetProperty("password").GetString()!;
        using var certificate = X509CertificateLoader.LoadPkcs12(
            pfx, password, X509KeyStorageFlags.EphemeralKeySet);

        Assert.Contains("Northstar Synthetic Demo", certificate.Subject);
        Assert.InRange(
            certificate.NotAfter.ToUniversalTime(),
            Now.AddDays(89).UtcDateTime,
            Now.AddDays(91).UtcDateTime);
        Assert.True(certificate.HasPrivateKey);
    }
}
