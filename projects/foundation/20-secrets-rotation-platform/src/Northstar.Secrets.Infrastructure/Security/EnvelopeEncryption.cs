using System.Security.Cryptography;
using System.Text;
using Northstar.Secrets.Application;

namespace Northstar.Secrets.Infrastructure.Security;

public sealed class LocalMasterKeyProviderOptions
{
    public const string DevelopmentDefault = "demo-only-not-a-real-secret-master-key-v1";
    public string KeyVersion { get; set; } = "local-v1";
    public string MasterKey { get; set; } = DevelopmentDefault;
}

public sealed class LocalMasterKeyProvider : IKeyProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private string _currentKeyVersion;

    public LocalMasterKeyProvider(LocalMasterKeyProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyVersion) || string.IsNullOrWhiteSpace(options.MasterKey))
        {
            throw new InvalidOperationException("A local master key and key version are required.");
        }

        _currentKeyVersion = options.KeyVersion;
        _keys[_currentKeyVersion] = NormalizeKey(Encoding.UTF8.GetBytes(options.MasterKey));
    }

    public string CurrentKeyVersion
    {
        get
        {
            lock (_gate)
            {
                return _currentKeyVersion;
            }
        }
    }

    public WrappedKey WrapKey(ReadOnlySpan<byte> dataEncryptionKey)
    {
        byte[] masterKey;
        string keyVersion;
        lock (_gate)
        {
            keyVersion = _currentKeyVersion;
            masterKey = _keys[keyVersion].ToArray();
        }

        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[dataEncryptionKey.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(masterKey, tag.Length);
            aes.Encrypt(
                nonce,
                dataEncryptionKey,
                ciphertext,
                tag,
                Encoding.UTF8.GetBytes(keyVersion));
            var packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length + tag.Length, ciphertext.Length);
            return new WrappedKey(packed, keyVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string keyVersion)
    {
        if (wrappedKey.Length < 29)
        {
            throw new CryptographicException("The wrapped data-encryption key is malformed.");
        }

        byte[] masterKey;
        lock (_gate)
        {
            if (!_keys.TryGetValue(keyVersion, out var stored))
            {
                throw new CryptographicException("The referenced master-key version is unavailable.");
            }

            masterKey = stored.ToArray();
        }

        try
        {
            var nonce = wrappedKey[..12];
            var tag = wrappedKey.Slice(12, 16);
            var ciphertext = wrappedKey[28..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(masterKey, tag.Length);
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                Encoding.UTF8.GetBytes(keyVersion));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public void RotateTo(string keyVersion, ReadOnlySpan<byte> keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(keyVersion))
        {
            throw new ArgumentException("A key version is required.", nameof(keyVersion));
        }

        lock (_gate)
        {
            _keys[keyVersion] = NormalizeKey(keyMaterial);
            _currentKeyVersion = keyVersion;
        }
    }

    private static byte[] NormalizeKey(ReadOnlySpan<byte> keyMaterial) =>
        keyMaterial.Length == 32 ? keyMaterial.ToArray() : SHA256.HashData(keyMaterial);
}

public sealed class EnvelopeEncryptionService(IKeyProvider keyProvider) : ISecretCipher
{
    public EncryptedPayload Encrypt(string plaintext, SecretBinding binding)
    {
        var dataEncryptionKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = new byte[plaintextBytes.Length];
            using var aes = new AesGcm(dataEncryptionKey, tag.Length);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, BuildAssociatedData(binding));
            var wrapped = keyProvider.WrapKey(dataEncryptionKey);
            return new EncryptedPayload(ciphertext, nonce, tag, wrapped.Value, wrapped.KeyVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    public string Decrypt(EncryptedPayload payload, SecretBinding binding)
    {
        var dataEncryptionKey = keyProvider.UnwrapKey(
            payload.WrappedDataEncryptionKey,
            payload.KeyVersion);
        try
        {
            var plaintext = new byte[payload.Ciphertext.Length];
            using var aes = new AesGcm(dataEncryptionKey, payload.AuthenticationTag.Length);
            aes.Decrypt(
                payload.Nonce,
                payload.Ciphertext,
                payload.AuthenticationTag,
                plaintext,
                BuildAssociatedData(binding));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    public WrappedKey RewrapDataEncryptionKey(byte[] wrappedKey, string previousKeyVersion)
    {
        var dataEncryptionKey = keyProvider.UnwrapKey(wrappedKey, previousKeyVersion);
        try
        {
            return keyProvider.WrapKey(dataEncryptionKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    private static byte[] BuildAssociatedData(SecretBinding binding) =>
        Encoding.UTF8.GetBytes(
            $"{binding.SecretId:N}|{binding.Name}|{binding.Type}|v{binding.VersionNumber}");
}
