using System.Security.Cryptography;
using AuditPlatform.Application.Abstractions;

namespace AuditPlatform.Infrastructure.Signing;

/// <summary>
/// Local RSA signing service, backed by an in-memory 2048-bit key generated per boot in
/// Development/Testing. Production deployments would replace this with an Azure Key Vault /
/// AWS KMS-backed adapter — the interface is deliberately narrow.
/// </summary>
public sealed class RsaSigningService : ISigningService, IDisposable
{
    private readonly RSA _rsa;
    public string KeyId { get; }

    public RsaSigningService(string keyId)
    {
        KeyId = keyId;
        _rsa = RSA.Create(2048);
    }

    public string SignBase64(byte[] payload)
    {
        var sig = _rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
    }

    public bool Verify(byte[] payload, string signatureBase64)
    {
        try
        {
            var sig = Convert.FromBase64String(signatureBase64);
            return _rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _rsa.Dispose();
}
