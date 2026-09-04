using Auth.Report;

namespace Auth.Tests;

/// <summary>
/// The migration curve and the generator underneath it.
///
/// A simulation that only agrees with itself proves nothing, so the central test here is
/// the one that checks the simulated curve against a closed form derived independently:
/// with per-user daily login probability p, the migrated fraction at day d is
/// 1 - E[(1-p)^d] over the cohort mixture. If those disagree, one of them is wrong, and
/// the report's headline numbers are downstream of both.
/// </summary>
public sealed class MigrationModelTests
{
    private const ulong Seed = 20260904UL;

    [Fact]
    public void Pcg32IsDeterministic()
    {
        var a = new Pcg32(42);
        var b = new Pcg32(42);
        for (var i = 0; i < 1000; i++) Assert.Equal(a.Next(), b.Next());
    }

    [Fact]
    public void Pcg32DiffersBySeed()
    {
        var a = new Pcg32(1);
        var b = new Pcg32(2);
        var same = 0;
        for (var i = 0; i < 1000; i++) if (a.Next() == b.Next()) same++;
        Assert.True(same < 5);
    }

    [Fact]
    public void Pcg32DiffersByStream()
    {
        var a = new Pcg32(1, 1);
        var b = new Pcg32(1, 2);
        var same = 0;
        for (var i = 0; i < 1000; i++) if (a.Next() == b.Next()) same++;
        Assert.True(same < 5);
    }

    [Fact]
    public void NextDoubleStaysInTheUnitInterval()
    {
        var rng = new Pcg32(7);
        for (var i = 0; i < 100_000; i++)
        {
            var value = rng.NextDouble();
            Assert.InRange(value, 0.0, 1.0);
            Assert.NotEqual(1.0, value);
        }
    }

    [Fact]
    public void NextDoubleIsRoughlyUniform()
    {
        // Ten buckets, 100k draws. Not a serious randomness test -- it is a smoke alarm for
        // the kind of mistake that makes every migration curve subtly wrong: a shift by the
        // wrong number of bits, a modulus where a multiply belonged.
        var rng = new Pcg32(11);
        var buckets = new int[10];
        for (var i = 0; i < 100_000; i++) buckets[(int)(rng.NextDouble() * 10)]++;

        Assert.All(buckets, b => Assert.InRange(b, 9_000, 11_000));
    }

    [Fact]
    public void NextIntIsBounded()
    {
        var rng = new Pcg32(13);
        for (var i = 0; i < 10_000; i++) Assert.InRange(rng.NextInt(7), 0, 6);
    }

    [Fact]
    public void EveryPopulationSharesSumToOne()
    {
        foreach (var population in Population.All)
        {
            Assert.Equal(1.0, population.Cohorts.Sum(c => c.Share), 6);
        }
    }

    [Fact]
    public void DailyLoginProbabilityComesFromAnExponentialInterArrivalTime()
    {
        // 1 - exp(-1/mean), not 1/mean. The distinction matters at the fast end: a cohort
        // that logs in on average every 1.2 days has a 56.5% chance of logging in on any
        // given day, not an impossible 83%. Getting this wrong makes the whole migration
        // curve optimistic in exactly the region the rollback window is measured in.
        Assert.Equal(1.0 - Math.Exp(-0.5), new Cohort("x", 1.0, 2.0).DailyLoginProbability, 9);
        Assert.Equal(0.5654, new Cohort("daily", 1.0, 1.2).DailyLoginProbability, 4);
        Assert.Equal(0.0, new Cohort("dormant", 1.0, double.PositiveInfinity).DailyLoginProbability);
    }

    [Fact]
    public void DailyLoginProbabilityIsAlwaysAProbability()
    {
        foreach (var population in Population.All)
        {
            Assert.All(population.Cohorts, c => Assert.InRange(c.DailyLoginProbability, 0.0, 1.0));
        }
    }

    [Fact]
    public void TheRunIsReproducible()
    {
        var a = MigrationModel.Run(Population.Typical, 2000, 100, Seed);
        var b = MigrationModel.Run(Population.Typical, 2000, 100, Seed);

        Assert.Equal(a.Days.Select(d => d.Migrated), b.Days.Select(d => d.Migrated));
    }

    [Fact]
    public void MigrationIsMonotoneAndBounded()
    {
        var run = MigrationModel.Run(Population.Typical, 5000, 365, Seed);

        for (var i = 1; i < run.Days.Count; i++)
        {
            Assert.True(run.Days[i].Migrated >= run.Days[i - 1].Migrated);
        }

        Assert.InRange(run.Days[^1].Migrated, 0, run.Users);
    }

    [Fact]
    public void TheSimulationAgreesWithTheClosedForm()
    {
        // The test that makes the rest of the numbers worth reading. 20,000 users is enough
        // that sampling noise is well under a percentage point.
        var run = MigrationModel.Run(Population.Typical, 20_000, 400, Seed);

        foreach (var day in new[] { 1, 7, 30, 90, 365 })
        {
            var simulated = run.FractionMigratedAt(day);
            var analytic = MigrationModel.ExpectedFractionMigrated(Population.Typical, day);
            Assert.True(Math.Abs(simulated - analytic) < 0.01,
                $"day {day}: simulated {simulated:F4}, analytic {analytic:F4}");
        }
    }

    [Fact]
    public void TheClosedFormAgreesForEveryPopulation()
    {
        foreach (var population in Population.All)
        {
            var run = MigrationModel.Run(population, 20_000, 400, Seed);
            var simulated = run.FractionMigratedAt(365);
            var analytic = MigrationModel.ExpectedFractionMigrated(population, 365);

            Assert.True(Math.Abs(simulated - analytic) < 0.015,
                $"{population.Name}: simulated {simulated:F4}, analytic {analytic:F4}");
        }
    }

    [Fact]
    public void TheCeilingIsTheNonDormantShare()
    {
        // Rehash-on-login migrates everybody who comes back. The asymptote is therefore the
        // share of the population that ever comes back, and the last few per cent are not
        // slow -- they are unreachable. That distinction decides whether the migration plan
        // needs a forced-reset phase, which is a business decision with a date on it.
        var dormant = Population.Typical.Cohorts
            .Where(c => double.IsPositiveInfinity(c.MeanDaysBetweenLogins))
            .Sum(c => c.Share);

        Assert.Equal(1.0 - dormant, MigrationModel.AsymptoticFractionMigrated(Population.Typical), 9);
        Assert.True(MigrationModel.AsymptoticFractionMigrated(Population.Typical) < 1.0);
    }

    [Fact]
    public void TheOptimisticPopulationActuallyFinishes()
    {
        Assert.Equal(1.0, MigrationModel.AsymptoticFractionMigrated(Population.Optimistic), 9);
    }

    [Fact]
    public void TheSeasonalPopulationNeverReachesNinetyPercent()
    {
        Assert.True(MigrationModel.AsymptoticFractionMigrated(Population.Seasonal) < 0.90);
        Assert.Null(MigrationModel.Run(Population.Seasonal, 20_000, 1095, Seed).DayReaching(0.90));
    }

    [Fact]
    public void DayReachingIsTheFirstDayAtOrAboveTheTarget()
    {
        var run = MigrationModel.Run(Population.Typical, 20_000, 1095, Seed);
        var day = run.DayReaching(0.5);

        Assert.NotNull(day);
        Assert.True(run.FractionMigratedAt(day!.Value) >= 0.5);
        Assert.True(day.Value == 0 || run.FractionMigratedAt(day.Value - 1) < 0.5);
    }

    [Fact]
    public void DayReachingIsNullWhenTheTargetIsUnreachable()
    {
        Assert.Null(MigrationModel.Run(Population.Typical, 20_000, 1095, Seed).DayReaching(0.99));
    }

    [Fact]
    public void TheRollbackWindowClosesWithinDays()
    {
        // Rehashing is one-way: the old hash is gone, so reverting means a forced reset for
        // everyone already rehashed. The window is measured in days and it closes fastest
        // exactly where the migration is healthiest.
        var typical = MigrationModel.Run(Population.Typical, 20_000, 1095, Seed);
        var optimistic = MigrationModel.Run(Population.Optimistic, 20_000, 1095, Seed);

        Assert.True(typical.LastDayBelow(0.05) < 14);
        Assert.True(optimistic.LastDayBelow(0.05) <= typical.LastDayBelow(0.05));
        Assert.True(typical.FractionMigratedAt(14) > 0.5);
    }

    [Fact]
    public void LastDayBelowIsConsistentWithTheCurve()
    {
        var run = MigrationModel.Run(Population.Typical, 20_000, 365, Seed);
        var last = run.LastDayBelow(0.25);

        Assert.True(run.FractionMigratedAt(last) < 0.25);
        Assert.True(run.FractionMigratedAt(last + 1) >= 0.25);
    }

    [Fact]
    public void SlidingSessionsDoNotAgeOut()
    {
        // Forms authentication renews its ticket on use, so an active session never
        // expires. "Wait for the old sessions to drain" is not a plan; the cutoff has to be
        // a date, and the accounts that survive longest are the most active ones.
        var (alive, expired) = SlidingSessionModel.SessionsAliveAfter(
            Population.Typical, 20_000, 30, 180, Seed);

        Assert.Equal(20_000, alive + expired);
        Assert.True(alive > 5_000, $"only {alive} sessions survived, expected the tail to be fat");
    }

    [Fact]
    public void ALongerTimeoutKeepsMoreSessionsAlive()
    {
        var (shortTimeout, _) = SlidingSessionModel.SessionsAliveAfter(
            Population.Typical, 20_000, 7, 180, Seed);
        var (longTimeout, _) = SlidingSessionModel.SessionsAliveAfter(
            Population.Typical, 20_000, 90, 180, Seed);

        Assert.True(longTimeout > shortTimeout);
    }

    [Fact]
    public void SessionSurvivalIsReproducible()
    {
        var a = SlidingSessionModel.SessionsAliveAfter(Population.Typical, 5_000, 30, 180, Seed);
        var b = SlidingSessionModel.SessionsAliveAfter(Population.Typical, 5_000, 30, 180, Seed);
        Assert.Equal(a, b);
    }

    [Fact]
    public void EveryUserIsAccountedForOnEveryDay()
    {
        var run = MigrationModel.Run(Population.Typical, 1_000, 30, Seed);
        Assert.All(run.Days, d => Assert.InRange(d.Migrated, 0, run.Users));
        Assert.All(run.Days, d => Assert.InRange(d.LoggedInToday, 0, run.Users));
    }

    [Fact]
    public void DayIndicesAreContiguousFromZero()
    {
        var run = MigrationModel.Run(Population.Typical, 100, 50, Seed);
        Assert.Equal(Enumerable.Range(0, run.Days.Count), run.Days.Select(d => d.Day));
    }
}
