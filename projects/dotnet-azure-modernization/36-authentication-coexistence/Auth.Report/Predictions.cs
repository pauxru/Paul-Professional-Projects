namespace Auth.Report;

public sealed record Prediction(int Number, string Claim, string Rationale);

/// <summary>
/// Written down before any of the experiments below were run, and not edited afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The value of this file is entirely in the ones that turn out to be wrong. Anybody can
/// produce a document where the measurements agree with the summary; that document is
/// evidence of nothing except that the summary was written last. Committing to a number
/// first makes the exercise falsifiable, and the surprises are where the engineering
/// judgement actually gets updated.
/// </para>
/// <para>
/// A prediction is scored "contradicted" only when the measurement disagrees with what was
/// written, not when it disagrees with what was meant.
/// </para>
/// </remarks>
public static class Predictions
{
    public static readonly Prediction[] All =
    [
        new(1,
            "Legacy authorization is monotone in the role set: adding a role never removes a permission.",
            "It is how role-based access control is described in every document that describes it, " +
            "and it is what the phrase 'grant a role' means."),

        new(2,
            "The naive role-to-scope transformation will diverge from the legacy policy on fewer " +
            "than 200 of the 4096 decisions.",
            "The grant table was written by reading the legacy code, so the common paths should " +
            "line up; divergence should be confined to a few odd corners."),

        new(3,
            "Divergences will run in both directions, with lockouts outnumbering escalations.",
            "Migrations lose rules. A lost grant rule locks somebody out, which is the failure " +
            "mode every migration post-mortem describes."),

        new(4,
            "The deny-aware transformation will reduce divergences substantially but not to zero.",
            "Transcribing negative clauses by hand is exactly the kind of work that misses one."),

        new(5,
            "Argon2id at the deployable RFC 9106 profile will cost between 30x and 100x a " +
            "PBKDF2-HMAC-SHA1 verification at 1000 iterations.",
            "1000 iterations of HMAC-SHA1 is a few hundred microseconds; Argon2id at 64 MiB " +
            "should land in the tens of milliseconds."),

        new(6,
            "Without padding work, a single timing sample from a failed login will identify " +
            "which hash format an account uses with better than 90% accuracy.",
            "The per-format costs differ by orders of magnitude, which is far more separation " +
            "than measurement noise on a warm process."),

        new(7,
            "With constant-work padding enabled, that accuracy falls to roughly chance (25% for " +
            "a four-way choice).",
            "If every path performs the target hash, there is nothing left to measure."),

        new(8,
            "Constant-work padding will roughly double the median verification time across a " +
            "mixed population.",
            "Half the population is already on Argon2id and pays nothing extra; the other half " +
            "pays one additional Argon2id."),

        new(9,
            "Rehash-on-login will migrate 95% of a typical population within 90 days.",
            "Most users of a line-of-business system sign in at least monthly."),

        new(10,
            "After three years, fewer than 1% of a typical population will remain unmigrated.",
            "Three years is long enough for even annual filers to come round twice."),

        new(11,
            "The rollback window -- the period during which fewer than 5% of users have been " +
            "rehashed -- will last at least two weeks.",
            "Five per cent of a population sounds like a lot of logins."),

        new(12,
            "Once every user's password has been rehashed to Argon2id, leaving the legacy Forms " +
            "ticket path enabled costs nothing.",
            "The credential is the thing being protected, and the credential has been upgraded."),

        new(13,
            "Legacy Forms tickets will all have expired within 60 days of the cutoff " +
            "announcement, given a 30-day sliding timeout.",
            "Two full timeout periods should drain any pool of sessions."),

        new(14,
            "Hardening the ticket protector to encrypt-then-MAC will leave at least one " +
            "externally distinguishable rejection reason.",
            "Expiry has to be reported differently, and error paths are hard to collapse " +
            "completely once a codebase has several of them."),
    ];
}
