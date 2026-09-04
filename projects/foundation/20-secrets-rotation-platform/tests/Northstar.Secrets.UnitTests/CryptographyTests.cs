using System.Security.Cryptography;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;
using Northstar.Secrets.Infrastructure.Security;

namespace Northstar.Secrets.UnitTests;

public sealed class CryptographyTests
{
    private static (LocalMasterKeyProvider Provider, EnvelopeEncryptionService Cipher) CreateCipher()
    {
        var provider = new LocalMasterKeyProvider(new LocalMasterKeyProviderOptions
        {
            KeyVersion = "v1",
            MasterKey = "demo-only-not-a-real-secret-cryptography-test-key"
        });
        return (provider, new EnvelopeEncryptionService(provider));
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginalValue()
    {
        var (_, cipher) = CreateCipher();
        var binding = new SecretBinding(Guid.NewGuid(), "orders/prod/api", SecretType.ApiKey, 1);
        var payload = cipher.Encrypt("runtime-generated-value", binding);

        Assert.Equal("runtime-generated-value", cipher.Decrypt(payload, binding));
    }

    [Fact]
    public void Decrypt_CiphertextTransplantedToDifferentSecret_RejectsAadMismatch()
    {
        var (_, cipher) = CreateCipher();
        var source = new SecretBinding(Guid.NewGuid(), "orders/prod/api", SecretType.ApiKey, 1);
        var target = new SecretBinding(Guid.NewGuid(), "billing/prod/api", SecretType.ApiKey, 1);
        var payload = cipher.Encrypt("runtime-generated-value", source);

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(payload, target));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_IsRejected()
    {
        var (_, cipher) = CreateCipher();
        var binding = new SecretBinding(Guid.NewGuid(), "orders/prod/api", SecretType.ApiKey, 1);
        var payload = cipher.Encrypt("runtime-generated-value", binding);
        payload.Ciphertext[0] ^= 0x40;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(payload, binding));
    }

    [Fact]
    public void Decrypt_TamperedAuthenticationTag_IsRejected()
    {
        var (_, cipher) = CreateCipher();
        var binding = new SecretBinding(Guid.NewGuid(), "orders/prod/api", SecretType.ApiKey, 1);
        var payload = cipher.Encrypt("runtime-generated-value", binding);
        payload.AuthenticationTag[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(payload, binding));
    }

    [Fact]
    public void Rewrap_MasterKeyRotation_PreservesCiphertextAndPlaintext()
    {
        var (provider, cipher) = CreateCipher();
        var binding = new SecretBinding(Guid.NewGuid(), "orders/prod/api", SecretType.ApiKey, 1);
        var payload = cipher.Encrypt("runtime-generated-value", binding);
        var ciphertextBefore = payload.Ciphertext.ToArray();
        provider.RotateTo("v2", RandomNumberGenerator.GetBytes(32));

        var wrapped = cipher.RewrapDataEncryptionKey(
            payload.WrappedDataEncryptionKey, payload.KeyVersion);
        var rewrapped = payload with
        {
            WrappedDataEncryptionKey = wrapped.Value,
            KeyVersion = wrapped.KeyVersion
        };

        Assert.Equal(ciphertextBefore, rewrapped.Ciphertext);
        Assert.Equal("v2", rewrapped.KeyVersion);
        Assert.Equal("runtime-generated-value", cipher.Decrypt(rewrapped, binding));
    }

    [Fact]
    public async Task LifecycleMasterKeyRotation_RewrapsEveryVersionWithoutChangingCiphertext()
    {
        await using var harness = await TestHarness.CreateAsync();
        await harness.RegisterAsync();
        var before = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);
        var ciphertext = before!.CurrentVersion!.Ciphertext.ToArray();

        var count = await harness.Lifecycle.RotateMasterKeyAsync(
            "test-v2",
            RandomNumberGenerator.GetBytes(32),
            CancellationToken.None);
        var after = await harness.Repository.GetSecretAsync(
            "orders/prod/database", CancellationToken.None);
        var current = after!.CurrentVersion!;
        var plaintext = harness.Cipher.Decrypt(
            current.ToEncryptedPayload(),
            after.ToBinding(current.VersionNumber));

        Assert.Equal(1, count);
        Assert.Equal(ciphertext, current.Ciphertext);
        Assert.Equal("test-v2", current.KeyVersion);
        Assert.False(string.IsNullOrWhiteSpace(plaintext));
    }
}
