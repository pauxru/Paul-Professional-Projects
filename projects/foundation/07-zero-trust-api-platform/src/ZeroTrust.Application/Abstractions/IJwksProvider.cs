namespace ZeroTrust.Application.Abstractions;

public sealed record JwksKey(string Kid, string Kty, string Use, string Alg, string N, string E);

public sealed record Jwks(IReadOnlyList<JwksKey> Keys);

public interface IJwksProvider
{
    Task<Jwks> GetAsync(CancellationToken ct);
    Task RotateAsync(CancellationToken ct);
}
