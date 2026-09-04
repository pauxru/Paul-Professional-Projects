using System.Diagnostics;
using System.Security.Cryptography;

namespace Auth.Passwords;

/// <summary>
/// The target work factor. Everything weaker than this is "legacy" by definition.
/// </summary>
public sealed record HashPolicy(
    int MemoryKib = 65536,
    int Iterations = 3,
    int Parallelism = 4,
    int SaltBytes = 16,
    int DigestBytes = 32)
{
    /// <summary>
    /// RFC 9106's second recommended option (64 MiB, t=3, p=4), which is the one you can
    /// actually deploy on a web tier: the first option (2 GiB) is correct and unusable,
    /// because a login endpoint holding 2 GiB per concurrent request is a denial of service
    /// waiting to be discovered by ordinary traffic.
    /// </summary>
    public static readonly HashPolicy Recommended = new();

    /// <summary>Small enough to run thousands of times in a test suite.</summary>
    public static readonly HashPolicy Fast = new(MemoryKib: 512, Iterations: 1, Parallelism: 1);
}

public enum VerifyOutcome
{
    /// <summary>Wrong password, or a stored hash that cannot be parsed.</summary>
    Failed,

    /// <summary>Correct password, and the stored hash already meets policy.</summary>
    SucceededCurrent,

    /// <summary>
    /// Correct password, stored under a weaker scheme. This is the only moment in the
    /// entire migration when the plaintext is available, so it is the only moment an
    /// upgrade is possible without involving the user.
    /// </summary>
    SucceededNeedsRehash,
}

public sealed record VerifyResult(
    VerifyOutcome Outcome,
    HashFormat? StoredFormat,
    string? UpgradedHash,
    double ElapsedMilliseconds)
{
    public bool Succeeded => Outcome != VerifyOutcome.Failed;
}

/// <summary>
/// Verifies a password against whatever is stored and, on success, hands back an upgraded
/// hash. This is "rehash on login": the migration rides on traffic the users generate
/// anyway, so nobody is ever emailed a reset link.
/// </summary>
/// <remarks>
/// <para>
/// The interesting property is not that it works. It is what it costs. Three things this
/// class makes measurable:
/// </para>
/// <list type="number">
/// <item>
/// The migration is driven by login frequency, which is a long-tailed distribution, so it
/// does not converge -- see <c>Auth.Report</c>'s cohort model.
/// </item>
/// <item>
/// The verification time depends on the stored format, so an unauthenticated attacker can
/// read the migration state of any account off the response latency. That is the reason for
/// <see cref="HashPolicy"/>-shaped padding work below.
/// </item>
/// <item>
/// Once the upgraded hash is written, the old one is gone. Rollback after that point is
/// not a deployment decision; it is a password reset for everyone who logged in.
/// </item>
/// </list>
/// </remarks>
public sealed class MigratingPasswordHasher
{
    private readonly HashPolicy _policy;
    private readonly bool _constantWork;
    private readonly Func<int, byte[]> _salt;

    /// <summary>
    /// A pre-computed hash of a value nobody knows, used to spend the same work on a
    /// missing account as on a real one. Without it, "user does not exist" returns in
    /// microseconds and user enumeration is free.
    /// </summary>
    private readonly StoredHash _decoy;

    public MigratingPasswordHasher(
        HashPolicy? policy = null,
        bool constantWork = false,
        Func<int, byte[]>? saltSource = null)
    {
        _policy = policy ?? HashPolicy.Recommended;
        _constantWork = constantWork;
        _salt = saltSource ?? RandomNumberGenerator.GetBytes;

        var decoySecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _decoy = PasswordAlgorithms.HashArgon2id(
            decoySecret, _salt(_policy.SaltBytes),
            _policy.MemoryKib, _policy.Iterations, _policy.Parallelism);
    }

    public HashPolicy Policy => _policy;

    public bool ConstantWork => _constantWork;

    public string HashNew(string password)
    {
        var hash = PasswordAlgorithms.HashArgon2id(
            password, _salt(_policy.SaltBytes),
            _policy.MemoryKib, _policy.Iterations, _policy.Parallelism);
        return HashCodec.Format(hash);
    }

    /// <summary>
    /// True when the stored hash is a different algorithm, or the same algorithm with
    /// parameters below current policy. The second half matters more than it looks: the
    /// point of rehash-on-login is not a single jump from PBKDF2 to Argon2id, it is a
    /// mechanism for raising the work factor forever as hardware gets faster.
    /// </summary>
    public bool NeedsRehash(StoredHash stored) =>
        stored.Format != HashFormat.Argon2id ||
        stored.MemoryKib < _policy.MemoryKib ||
        stored.Iterations < _policy.Iterations ||
        stored.Digest.Length < _policy.DigestBytes;

    public VerifyResult Verify(string? storedHash, string password)
    {
        var clock = Stopwatch.StartNew();

        StoredHash? stored = null;
        if (storedHash is not null)
        {
            try
            {
                stored = HashCodec.Parse(storedHash);
            }
            catch (FormatException)
            {
                stored = null;
            }
        }

        // No account, or an unreadable hash. Verify the decoy anyway so the caller cannot
        // tell this branch from a wrong password on a real Argon2id account.
        if (stored is null)
        {
            PasswordAlgorithms.Verify(_decoy, password);
            return new VerifyResult(VerifyOutcome.Failed, null, null, Finish(clock, spentTarget: true));
        }

        var ok = PasswordAlgorithms.Verify(stored, password);

        // The padding has to happen on both the success and failure paths, and before the
        // result is known to the caller. Doing it only on failure would replace one oracle
        // with another.
        var spentTarget = stored.Format == HashFormat.Argon2id &&
                          stored.MemoryKib >= _policy.MemoryKib &&
                          stored.Iterations >= _policy.Iterations;
        if (_constantWork && !spentTarget)
        {
            PasswordAlgorithms.Verify(_decoy, password);
            spentTarget = true;
        }

        if (!ok)
        {
            return new VerifyResult(VerifyOutcome.Failed, stored.Format, null, Finish(clock, spentTarget));
        }

        if (!NeedsRehash(stored))
        {
            return new VerifyResult(
                VerifyOutcome.SucceededCurrent, stored.Format, null, Finish(clock, spentTarget));
        }

        return new VerifyResult(
            VerifyOutcome.SucceededNeedsRehash, stored.Format, HashNew(password),
            Finish(clock, spentTarget));
    }

    private static double Finish(Stopwatch clock, bool spentTarget)
    {
        _ = spentTarget;
        clock.Stop();
        return clock.Elapsed.TotalMilliseconds;
    }
}
