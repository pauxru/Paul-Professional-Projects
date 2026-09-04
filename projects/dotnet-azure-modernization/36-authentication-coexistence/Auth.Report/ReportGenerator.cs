using System.Globalization;
using System.Text;
using Auth.Bridge;
using Auth.Passwords;

namespace Auth.Report;

/// <summary>
/// Runs every experiment and writes the two report files.
/// </summary>
/// <remarks>
/// Two files rather than one, for the reason set out in
/// <c>docs/adr/005-two-report-files.md</c>: <c>results.md</c> carries every number including
/// the wall-clock timings, and <c>results-stable.md</c> carries only what is deterministic.
/// The test harness regenerates the stable file and compares it byte for byte against the
/// committed copy, which turns "the findings still hold" into something a build can fail on.
/// A single file containing timings could never be compared that way, and a single file
/// without them would be missing the report's sharpest finding.
/// </remarks>
public static class ReportGenerator
{
    private const int TimingSamplesPerFormat = 60;
    private const int SimulatedUsers = 20000;
    private const int SimulatedDays = 1095;
    private const ulong Seed = 20260904;

    public static string Build(bool stable)
    {
        var text = new StringBuilder();

        void Line(string value = "") => text.Append(value).Append('\n');

        Line("# Authentication coexistence: measured results");
        Line();
        Line(stable
            ? "Deterministic findings only. Regenerated and byte-compared by `test.ps1` stage 5."
            : "Full results, including wall-clock timings. See `results-stable.md` for the "
              + "subset a build can compare byte for byte.");
        Line();

        // ---------------------------------------------------------------- claims divergence
        var naive = Experiments.Divergences(new NaiveClaimsTransformer());
        var denyAware = Experiments.Divergences(new DenyAwareClaimsTransformer());
        var corrected = Experiments.Divergences(new CorrectedClaimsTransformer());
        var monotonicity = Experiments.Monotonicity();

        Line("## 1. The claims transformation cannot be finished");
        Line();
        Line($"Decision space: {DivergenceAnalysis.TotalDecisions} decisions "
             + $"({1 << LegacyRoles.All.Length} role sets x 8 request contexts x {Resources.All.Length} resources). "
             + "Every one is evaluated; nothing is sampled.");
        Line();
        Line("| transformation | divergent decisions | escalations | lockouts |");
        Line("| --- | ---: | ---: | ---: |");
        Line($"| naive role-to-scope table | {naive.Total} | {naive.Escalations} | {naive.Lockouts} |");
        Line($"| + deny channel | {denyAware.Total} | {denyAware.Escalations} | {denyAware.Lockouts} |");
        Line($"| + one grant row corrected | {corrected.Total} | {corrected.Escalations} | {corrected.Lockouts} |");
        Line();

        Line($"Legacy authorization is **not monotone** in the role set: "
             + $"{monotonicity.Count} distinct (role set, added role, resource) triples exist where "
             + "granting an additional role *removes* a permission. Examples:");
        Line();
        Line("| roles | plus one more role | loses |");
        Line("| --- | --- | --- |");
        foreach (var (smaller, larger, resource) in monotonicity.Take(6))
        {
            Line($"| `{smaller}` | `{larger}` | `{resource}` |");
        }
        Line();
        Line("A role-to-scope table unions scopes across roles, so it is monotone by "
             + "construction. A monotone function cannot agree everywhere with a non-monotone "
             + "one. The naive transformation is therefore not a transformation with bugs in "
             + "it -- it is one that provably cannot be completed, no matter how many rows are "
             + "added to the table.");
        Line();

        if (naive.Total > 0)
        {
            var direction = naive.Lockouts == 0
                ? "**Every single divergence grants access the legacy system denied. Not one denies "
                  + "access the legacy system granted.**"
                : $"{naive.Escalations} grant access the legacy system denied; "
                  + $"{naive.Lockouts} deny access it granted.";
            Line(direction);
            Line();
            Line("That direction is the finding. A migration whose failures are all lockouts "
                 + "generates support tickets on day one and gets fixed. A migration whose "
                 + "failures are all escalations passes user acceptance testing, because no "
                 + "user has ever reported a door that should have been locked.");
            Line();
            Line("| kind | roles | resource | local network | tenant matches | temp staff |");
            Line("| --- | --- | --- | --- | --- | --- |");
            foreach (var example in naive.Examples)
            {
                Line($"| {example.Kind} | `{example.RoleList}` | `{example.Resource}` | "
                     + $"{example.Context.IsLocalNetwork} | {example.Context.TenantMatches} | "
                     + $"{example.Context.IsTemporaryStaff} |");
            }
            Line();
        }

        Line($"Adding a negative channel -- roles that *remove* a scope, with deny winning -- "
             + $"brings divergence from {naive.Total} to **{denyAware.Total}**. The fix is a change "
             + "of shape, not a longer table.");
        Line();
        Line($"The {denyAware.Total} that survive are a different animal, and it is worth being "
             + "precise about which is which. They are not a limit of the deny channel: they are "
             + $"one wrong row in the grant table -- `Temp` was given `{Resources.ReadLedger}`, "
             + "which the legacy policy never granted it. Correcting that single row takes the "
             + $"count to **{corrected.Total}**.");
        Line();
        Line($"So of the original {naive.Total} divergences, "
             + $"{naive.Total - denyAware.Total} were structural -- unreachable by any grant table, "
             + $"however carefully reviewed -- and {denyAware.Total} were an ordinary data error. "
             + "Both produce identical symptoms. Only one of them is fixed by reviewing the "
             + "mapping more carefully, which is the remedy invariably proposed for both.");
        Line();

        // ------------------------------------------------------------------- timing channel
        TimingChannelResult? leaky = null;
        TimingChannelResult? padded = null;
        if (!stable)
        {
            Line("## 2. Verification time tells an anonymous caller who is still unmigrated");
            Line();

            leaky = Experiments.TimingChannel(false, TimingSamplesPerFormat, HashPolicy.Recommended);
            padded = Experiments.TimingChannel(true, TimingSamplesPerFormat, HashPolicy.Recommended);

            Line($"{TimingSamplesPerFormat} verifications per format, all with a wrong password, "
                 + "against the RFC 9106 deployable profile (64 MiB, t=3, p=4). A nearest-median "
                 + "classifier is fitted on the first half of the samples and scored on the second.");
            Line();
            Line("| stored format | median ms, no padding | median ms, constant work |");
            Line("| --- | ---: | ---: |");
            foreach (var format in new[]
                     {
                         HashFormat.MembershipSha1, HashFormat.IdentityV2,
                         HashFormat.IdentityV3, HashFormat.Argon2id,
                     })
            {
                Line($"| {format} | {leaky.MedianMilliseconds[format].ToString("F3", CultureInfo.InvariantCulture)} "
                     + $"| {padded.MedianMilliseconds[format].ToString("F3", CultureInfo.InvariantCulture)} |");
            }
            Line();

            var argonOverPbkdf1 = leaky.MedianMilliseconds[HashFormat.Argon2id]
                                / leaky.MedianMilliseconds[HashFormat.IdentityV2];
            Line($"Argon2id costs **{argonOverPbkdf1.ToString("F0", CultureInfo.InvariantCulture)}x** a "
                 + "PBKDF2-HMAC-SHA1 verification at 1000 iterations, and that ratio counts only "
                 + "time. The memory term -- 64 MiB per verification against a few hundred bytes -- "
                 + "is the part an attacker with a warehouse of GPUs actually feels, and it does "
                 + "not appear in this column at all.");
            Line();
            Line("| measurement | no padding | constant work | base rate |");
            Line("| --- | ---: | ---: | ---: |");
            Line($"| four-way format identification | {Pct(leaky.FourWayAccuracy)} "
                 + $"| {Pct(padded.FourWayAccuracy)} | 25.0% |");
            Line($"| \"is this account still unmigrated\" | {Pct(leaky.UnmigratedDetectionAccuracy)} "
                 + $"| {Pct(padded.UnmigratedDetectionAccuracy)} | 75.0% |");
            Line();
            Line("The base-rate column is there to stop the middle column being over-read. Three "
                 + "of the four formats are legacy, so a classifier that has learned nothing and "
                 + "guesses uniformly still scores 75% on the binary question. With padding on, "
                 + "the measured figure sits at or below that line: the remaining accuracy is "
                 + "arithmetic, not signal.");
            Line();
            Line("The attack is reconnaissance, not password recovery: submit a deliberately "
                 + "wrong password to the ordinary login endpoint and read the clock. It needs "
                 + "no credentials, leaves ordinary failed-login noise, and turns a future "
                 + "database breach from \"crack whatever you can\" into a target list of the "
                 + "accounts whose hashes are weakest -- prepared in advance.");
            Line();

            // What the mitigation costs a real population, rather than a uniform mix of
            // formats that no database actually contains.
            var curve = MigrationModel.Run(Population.Typical, SimulatedUsers, SimulatedDays, Seed);
            var legacyMedian = Median3(
                leaky.MedianMilliseconds[HashFormat.MembershipSha1],
                leaky.MedianMilliseconds[HashFormat.IdentityV2],
                leaky.MedianMilliseconds[HashFormat.IdentityV3]);
            var argonMedian = leaky.MedianMilliseconds[HashFormat.Argon2id];

            Line("The cost of the mitigation depends on how far the migration has got, and it "
                 + "moves in the direction nobody expects:");
            Line();
            Line("| day | migrated | mean verification, no padding | with padding | overhead |");
            Line("| ---: | ---: | ---: | ---: | ---: |");
            foreach (var day in new[] { 1, 30, 90, 365, 1095 })
            {
                var migrated = curve.FractionMigratedAt(day);
                var without = migrated * argonMedian + (1 - migrated) * legacyMedian;
                var with = argonMedian;
                Line($"| {day} | {Pct(migrated)} "
                     + $"| {without.ToString("F1", CultureInfo.InvariantCulture)} ms "
                     + $"| {with.ToString("F1", CultureInfo.InvariantCulture)} ms "
                     + $"| {PopulationOverhead(curve, day, argonMedian, legacyMedian).ToString("F2", CultureInfo.InvariantCulture)}x |");
            }
            Line();
            Line("Padding is most expensive on day one and converges to free. It is a cost that "
                 + "retires itself as the migration proceeds -- which is the opposite of the "
                 + "usual argument for deferring a mitigation until the migration is finished. "
                 + "Deferring it means paying nothing precisely when there is nothing to hide, "
                 + "and leaving the channel open precisely when it discloses the most.");
            Line();
        }

        // ------------------------------------------------------------------- migration tail
        Line(stable ? "## 2. Rehash-on-login does not converge" : "## 3. Rehash-on-login does not converge");
        Line();
        Line($"{SimulatedUsers:N0} simulated users, {SimulatedDays} days, seeded PCG32 "
             + $"(seed {Seed}). Users are assigned to cohorts by login frequency; a user migrates "
             + "on their first login after the feature ships.");
        Line();
        Line("| population | day to 50% | to 90% | to 95% | to 99% | at 1 year | at 3 years | ceiling |");
        Line("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        var runs = new Dictionary<string, MigrationRun>();
        foreach (var population in Population.All)
        {
            var run = MigrationModel.Run(population, SimulatedUsers, SimulatedDays, Seed);
            runs[population.Name] = run;
            Line($"| {population.Name} | {Day(run.DayReaching(0.50))} | {Day(run.DayReaching(0.90))} | "
                 + $"{Day(run.DayReaching(0.95))} | {Day(run.DayReaching(0.99))} | "
                 + $"{Pct(run.FractionMigratedAt(365))} | {Pct(run.FractionMigratedAt(1095))} | "
                 + $"{Pct(MigrationModel.AsymptoticFractionMigrated(population))} |");
        }
        Line();

        var typical = runs["typical"];
        Line($"The simulation agrees with the closed form it should agree with: at day 365 the "
             + $"model gives {Pct(typical.FractionMigratedAt(365))} and the analytic mixture "
             + $"1 - E[(1-p)^d] gives "
             + $"{Pct(MigrationModel.ExpectedFractionMigrated(Population.Typical, 365))}. "
             + "The curve is not a fixed seed agreeing with itself.");
        Line();
        Line("The ceiling column is the part that matters. Any cohort that never signs in never "
             + "migrates, so the asymptote sits strictly below 100% and the last few per cent "
             + "are not slow -- they are unreachable. Rehash-on-login is a mechanism for "
             + "migrating everybody who comes back, and it must be paired from day one with a "
             + "dated plan for everybody who does not.");
        Line();

        // ---------------------------------------------------------------- rollback window
        Line(stable ? "## 3. The rollback window closes before the evidence arrives"
                    : "## 4. The rollback window closes before the evidence arrives");
        Line();
        Line("Rehashing is one-way. Reverting to the previous scheme after day D means a forced "
             + "password reset for everyone rehashed by day D -- there is no third option, "
             + "because the old hash no longer exists. (Keeping it would reintroduce exactly the "
             + "weakness the migration removed.)");
        Line();
        Line("| population | last day under 1% rehashed | under 5% | under 25% | rehashed by day 14 |");
        Line("| --- | ---: | ---: | ---: | ---: |");
        foreach (var population in Population.All)
        {
            var run = runs[population.Name];
            Line($"| {population.Name} | {run.LastDayBelow(0.01)} | {run.LastDayBelow(0.05)} | "
                 + $"{run.LastDayBelow(0.25)} | {Pct(run.FractionMigratedAt(14))} |");
        }
        Line();
        Line("The window is measured in days, and it is shortest exactly where the migration is "
             + "healthiest -- a population that signs in often migrates fast and becomes "
             + "un-revertible fast. By the time there is enough production evidence to be "
             + "confident the change was safe, the option to undo it has already gone. Plan the "
             + "rollback for the first week or do not claim to have one.");
        Line();

        // --------------------------------------------------------------- downgrade exposure
        var (during, after, subject) = Experiments.DowngradeExposure();

        Line(stable ? "## 4. A migrated account is only as strong as the weakest door left open"
                    : "## 5. A migrated account is only as strong as the weakest door left open");
        Line();
        Line($"`{subject}` holds an Argon2id hash of a long passphrase and signs in through OIDC. "
             + "Assuming the legacy validation key has leaked -- it lives in `web.config`, it is "
             + "identical across the farm, and in a system this old it is usually in source "
             + "control history -- a forged Forms ticket for that account is:");
        Line();
        Line($"- accepted during coexistence: **{during}**");
        Line($"- accepted after the legacy path is switched off: **{after}**");
        Line();
        Line("The password migration bought this account nothing while the ticket path stayed "
             + "open. Account security is the minimum over every enabled authentication path, "
             + "and the credential path is the only one anybody measures. Every stack you keep "
             + "running for the sake of the stragglers is running for everybody.");
        Line();

        var (stillAlive, expired) = SlidingSessionModel.SessionsAliveAfter(
            Population.Typical, SimulatedUsers, timeoutDays: 30, days: 180, seed: Seed);
        Line($"Nor can that path be closed by waiting. Forms authentication renews its ticket on "
             + $"use, so an active session never ages out. 180 days after a cutoff is announced, "
             + $"with a 30-day sliding timeout, {stillAlive:N0} of {SimulatedUsers:N0} simulated "
             + $"sessions are still valid and {expired:N0} have lapsed -- and the survivors are "
             + "the most active accounts, which is to say the most valuable ones. A sliding "
             + "window has no natural end; the cutoff has to be a date.");
        Line();

        // ----------------------------------------------------------------------- hardening
        var (legacyReasons, hardenedReasons) = Experiments.RejectionReasons();
        var tokenChecks = Experiments.TokenHardening();
        var federationChecks = Experiments.FederationHardening();
        var agree = Experiments.ThreeStacksAgree();

        Line(stable ? "## 5. What the hardened stacks refuse" : "## 6. What the hardened stacks refuse");
        Line();
        Line($"Distinct rejection reasons observable from outside, over four tampered tickets: "
             + $"legacy protector **{legacyReasons.Distinct().Count()}** "
             + $"({string.Join(", ", legacyReasons.Distinct())}), "
             + $"hardened protector **{hardenedReasons.Distinct().Count()}** "
             + $"({string.Join(", ", hardenedReasons.Distinct())}).");
        Line();
        Line("The legacy composition authenticates the plaintext and then encrypts the result, "
             + "so integrity cannot be checked before decryption and the unpadding step runs on "
             + "attacker-controlled bytes. Encrypt-then-MAC removes the distinction by making "
             + "the dangerous code unreachable rather than by handling its error paths "
             + "consistently -- a property that survives future edits instead of needing to be "
             + "re-established by each one.");
        Line();

        var allChecks = tokenChecks.Concat(federationChecks).ToList();
        Line($"Token and assertion checks: **{allChecks.Count(c => c.Rejected)} of {allChecks.Count}** "
             + "behave as required.");
        Line();
        Line("| check | holds |");
        Line("| --- | --- |");
        foreach (var (name, held) in allChecks) Line($"| {name} | {held} |");
        Line();
        Line($"All three stacks produce an identical canonical principal for the same user: "
             + $"**{agree}**. That is the functional requirement -- and, per section "
             + $"{(stable ? "4" : "5")}, the reason the security property above is what it is.");
        Line();

        // --------------------------------------------------------------------- predictions
        Line(stable ? "## 6. Predictions" : "## 7. Predictions");
        Line();
        var verdicts = Score(naive, denyAware, monotonicity.Count, runs, during, after,
                             stillAlive, hardenedReasons.Distinct().Count(), stable, leaky, padded);
        var wrong = verdicts.Count(v => v.Outcome == "contradicted");
        Line($"{verdicts.Count} predictions were written before the experiments were run. "
             + $"**{wrong} were contradicted.**");
        Line();
        Line("| # | prediction | verdict | what actually happened |");
        Line("| ---: | --- | --- | --- |");
        foreach (var v in verdicts)
        {
            Line($"| {v.Number} | {v.Claim} | {v.Outcome} | {v.Actual} |");
        }
        Line();

        return text.ToString();
    }

    private sealed record Verdict(int Number, string Claim, string Outcome, string Actual);

    private static List<Verdict> Score(
        DivergenceSummary naive, DivergenceSummary denyAware, int counterexamples,
        Dictionary<string, MigrationRun> runs, bool during, bool after,
        int stillAlive, int hardenedReasonCount, bool stable,
        TimingChannelResult? leaky, TimingChannelResult? padded)
    {
        var typical = runs["typical"];
        var verdicts = new List<Verdict>();

        void Add(int number, bool held, string actual)
        {
            var prediction = Predictions.All.Single(p => p.Number == number);
            verdicts.Add(new Verdict(number, prediction.Claim, held ? "held" : "contradicted", actual));
        }

        Add(1, counterexamples == 0,
            $"{counterexamples} counterexamples to monotonicity");
        Add(2, naive.Total < 200,
            $"{naive.Total} of {DivergenceAnalysis.TotalDecisions} decisions diverge");
        Add(3, naive.Lockouts > naive.Escalations,
            $"{naive.Escalations} escalations, {naive.Lockouts} lockouts");
        Add(4, denyAware.Total > 0,
            $"{denyAware.Total} divergences remain, all from one wrong grant row");

        if (stable || leaky is null || padded is null)
        {
            // Timing-derived verdicts are omitted from the stable file rather than recorded
            // with a wall-clock number that would change between runs.
            foreach (var number in new[] { 5, 6, 7, 8 })
            {
                verdicts.Add(new Verdict(number, Predictions.All.Single(p => p.Number == number).Claim,
                    "timing", "see results.md"));
            }
        }
        else
        {
            var ratio = leaky.MedianMilliseconds[HashFormat.Argon2id]
                      / leaky.MedianMilliseconds[HashFormat.IdentityV2];
            Add(5, ratio is >= 30 and <= 100, Times(ratio));

            Add(6, leaky.FourWayAccuracy > 0.90,
                $"{Pct(leaky.FourWayAccuracy)} four-way identification from one sample");

            // Chance for the four-way question is 25%. Anything near it means the channel is
            // closed; the binary figure is checked against its own base rate of 75%.
            Add(7, padded.FourWayAccuracy < 0.40 && padded.UnmigratedDetectionAccuracy <= 0.80,
                $"{Pct(padded.FourWayAccuracy)} four-way (chance 25.0%), "
                + $"{Pct(padded.UnmigratedDetectionAccuracy)} binary (base rate 75.0%)");

            var legacyMedian = Median3(
                leaky.MedianMilliseconds[HashFormat.MembershipSha1],
                leaky.MedianMilliseconds[HashFormat.IdentityV2],
                leaky.MedianMilliseconds[HashFormat.IdentityV3]);
            var argonMedian = leaky.MedianMilliseconds[HashFormat.Argon2id];

            // Scored against a realistic population rather than the four-format sample, which
            // weights the legacy formats 3:1 and would flatter the prediction for the wrong
            // reason. "Roughly double" is only true if the overhead is a constant; it is not.
            var day1 = PopulationOverhead(typical, 1, argonMedian, legacyMedian);
            var day30 = PopulationOverhead(typical, 30, argonMedian, legacyMedian);
            var day365 = PopulationOverhead(typical, 365, argonMedian, legacyMedian);
            Add(8, day1 is >= 1.5 and <= 3.0 && day365 is >= 1.5 and <= 3.0,
                $"{Times(day1)} on day 1, {Times(day30)} on day 30, {Times(day365)} at one year "
                + "-- the overhead is time-varying, not a constant");
        }

        Add(9, typical.DayReaching(0.95) is <= 90,
            typical.DayReaching(0.95) is { } d95
                ? $"95% reached on day {d95}"
                : $"95% never reached; {Pct(typical.FractionMigratedAt(1095))} at 3 years");
        Add(10, 1.0 - typical.FractionMigratedAt(1095) < 0.01,
            $"{Pct(1.0 - typical.FractionMigratedAt(1095))} still unmigrated at 3 years");
        Add(11, typical.LastDayBelow(0.05) >= 14,
            $"under 5% only until day {typical.LastDayBelow(0.05)}; "
            + $"{Pct(typical.FractionMigratedAt(1))} already rehashed after one day");
        Add(12, !during || after,
            during
                ? "a forged legacy ticket still authenticates the fully migrated account"
                : "the legacy path did not authenticate the account");
        Add(13, stillAlive == 0,
            $"{stillAlive:N0} sessions still valid after 180 days");
        Add(14, hardenedReasonCount > 1,
            $"{hardenedReasonCount} distinct rejection reason"
            + (hardenedReasonCount == 1 ? "" : "s") + " from the hardened protector");

        return verdicts.OrderBy(v => v.Number).ToList();
    }

    private static string Pct(double value) =>
        (value * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static double Median3(double a, double b, double c) =>
        Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    private static string Times(double value) =>
        value.ToString(value < 10 ? "F2" : "F0", CultureInfo.InvariantCulture) + "x";

    /// <summary>
    /// Cost of constant-work padding to a population that is <paramref name="day"/> days into
    /// the migration. Padding makes every verification cost an Argon2id; without it, the mean
    /// is the migrated fraction on Argon2id and the rest on a legacy hash. The ratio therefore
    /// falls as the migration proceeds, which is the point.
    /// </summary>
    private static double PopulationOverhead(
        MigrationRun curve, int day, double argonMedian, double legacyMedian)
    {
        var migrated = curve.FractionMigratedAt(day);
        var without = migrated * argonMedian + (1 - migrated) * legacyMedian;
        return argonMedian / without;
    }

    private static string Day(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "never";
}
