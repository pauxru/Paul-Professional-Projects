namespace Auth.Report;

/// <summary>
/// How often a population of real users actually signs in.
/// </summary>
/// <remarks>
/// <para>
/// The single assumption that decides whether rehash-on-login is a plan or a wish. If login
/// frequency were uniform, the migration would complete on a predictable date and everything
/// downstream would be scheduling. It is not uniform: it is long-tailed, and the tail
/// contains the service accounts, the seasonal staff, the people who left, and the users who
/// only touch the system when something breaks.
/// </para>
/// <para>
/// These shares come from the shape of enterprise line-of-business usage rather than from any
/// one dataset, and they are the load-bearing assumption of the entire migration timeline --
/// so <c>Auth.Report</c> re-runs the model across several populations to show which
/// conclusions survive changing them and which do not.
/// </para>
/// </remarks>
public sealed record Cohort(string Name, double Share, double MeanDaysBetweenLogins)
{
    /// <summary>Daily probability of at least one login, from an exponential inter-arrival time.</summary>
    public double DailyLoginProbability =>
        double.IsPositiveInfinity(MeanDaysBetweenLogins)
            ? 0.0
            : 1.0 - Math.Exp(-1.0 / MeanDaysBetweenLogins);
}

public sealed record Population(string Name, IReadOnlyList<Cohort> Cohorts)
{
    public static readonly Population Typical = new("typical",
    [
        new Cohort("daily operators", 0.22, 1.2),
        new Cohort("weekly staff", 0.34, 7.0),
        new Cohort("monthly approvers", 0.21, 30.0),
        new Cohort("quarterly reviewers", 0.13, 91.0),
        new Cohort("annual filers", 0.06, 365.0),
        new Cohort("dormant and service accounts", 0.04, double.PositiveInfinity),
    ]);

    /// <summary>A friendlier population: more daily users, no dormant accounts at all.</summary>
    public static readonly Population Optimistic = new("optimistic",
    [
        new Cohort("daily operators", 0.45, 1.1),
        new Cohort("weekly staff", 0.40, 6.0),
        new Cohort("monthly approvers", 0.13, 28.0),
        new Cohort("quarterly reviewers", 0.02, 90.0),
    ]);

    /// <summary>A large seasonal workforce and a big pile of forgotten accounts.</summary>
    public static readonly Population Seasonal = new("seasonal",
    [
        new Cohort("daily operators", 0.10, 1.5),
        new Cohort("weekly staff", 0.18, 8.0),
        new Cohort("monthly approvers", 0.17, 32.0),
        new Cohort("seasonal staff", 0.30, 150.0),
        new Cohort("annual filers", 0.13, 365.0),
        new Cohort("dormant and service accounts", 0.12, double.PositiveInfinity),
    ]);

    public static readonly Population[] All = [Typical, Optimistic, Seasonal];
}

public sealed record MigrationDay(int Day, int Migrated, int LoggedInToday);

public sealed record MigrationRun(
    Population Population,
    int Users,
    IReadOnlyList<MigrationDay> Days)
{
    public double FractionMigratedAt(int day) =>
        (double)Days[Math.Min(day, Days.Count - 1)].Migrated / Users;

    /// <summary>First day on which the migrated fraction reaches <paramref name="target"/>, or null.</summary>
    public int? DayReaching(double target)
    {
        foreach (var day in Days)
        {
            if ((double)day.Migrated / Users >= target) return day.Day;
        }
        return null;
    }

    /// <summary>
    /// Last day on which no more than <paramref name="fraction"/> of users have been
    /// rehashed -- i.e. the last day a rollback would require fewer than that many password
    /// resets.
    /// </summary>
    public int LastDayBelow(double fraction)
    {
        var last = 0;
        foreach (var day in Days)
        {
            if ((double)day.Migrated / Users <= fraction) last = day.Day;
            else break;
        }
        return last;
    }
}

/// <summary>
/// Simulates rehash-on-login across a population, day by day.
/// </summary>
/// <remarks>
/// Deliberately the simplest model that can be wrong in an interesting way. No seasonality,
/// no correlation between users, no account churn. Adding those would move the numbers; the
/// finding the report draws from this model is about the <i>shape</i> of the curve, which is
/// determined by the long tail and survives every refinement that keeps the tail.
/// </remarks>
public static class MigrationModel
{
    public static MigrationRun Run(Population population, int users, int days, ulong seed)
    {
        var rng = new Pcg32(seed);

        var rates = new double[users];
        var assigned = 0;
        for (var i = 0; i < population.Cohorts.Count; i++)
        {
            var cohort = population.Cohorts[i];
            var count = i == population.Cohorts.Count - 1
                ? users - assigned
                : (int)Math.Round(cohort.Share * users);
            for (var j = 0; j < count && assigned < users; j++, assigned++)
            {
                rates[assigned] = cohort.DailyLoginProbability;
            }
        }

        var migrated = new bool[users];
        var migratedCount = 0;
        var timeline = new List<MigrationDay>(days + 1) { new(0, 0, 0) };

        for (var day = 1; day <= days; day++)
        {
            var loggedIn = 0;
            for (var user = 0; user < users; user++)
            {
                // Everyone keeps logging in after they migrate. Drawing for migrated users
                // too keeps each user's random stream independent of when they migrated,
                // so the same seed gives the same login pattern under any configuration.
                var logsIn = rng.NextBool(rates[user]);
                if (!logsIn) continue;
                loggedIn++;
                if (migrated[user]) continue;
                migrated[user] = true;
                migratedCount++;
            }

            timeline.Add(new MigrationDay(day, migratedCount, loggedIn));
        }

        return new MigrationRun(population, users, timeline);
    }

    /// <summary>
    /// The closed form the simulation should agree with: a user with daily login probability
    /// p is unmigrated after d days with probability (1-p)^d, so the expected migrated
    /// fraction is 1 - E[(1-p)^d] over the cohort mixture.
    /// </summary>
    /// <remarks>
    /// Present so the simulation can be checked against something that is not itself a
    /// simulation. A Monte Carlo run that agrees with an independently derived expectation is
    /// evidence; one that only agrees with itself is a fixed seed.
    /// </remarks>
    public static double ExpectedFractionMigrated(Population population, int day)
    {
        var remaining = 0.0;
        foreach (var cohort in population.Cohorts)
        {
            remaining += cohort.Share * Math.Pow(1.0 - cohort.DailyLoginProbability, day);
        }
        return 1.0 - remaining;
    }

    /// <summary>
    /// The asymptote. Anything with an infinite mean inter-arrival time never migrates, so
    /// this is strictly below 1 whenever the population contains dormant accounts.
    /// </summary>
    public static double AsymptoticFractionMigrated(Population population) =>
        1.0 - population.Cohorts
            .Where(c => double.IsPositiveInfinity(c.MeanDaysBetweenLogins))
            .Sum(c => c.Share);
}

/// <summary>
/// Legacy session lifetime under Forms authentication's sliding expiration.
/// </summary>
/// <remarks>
/// Sliding expiration renews the ticket on use. For a user who signs in regularly, the
/// ticket is therefore never more than one timeout away from expiring and never actually
/// expires. The consequence for the migration is structural: the legacy acceptance window
/// cannot be closed by waiting for tickets to lapse, because the tickets belonging to the
/// most active users -- who are also the highest-value targets -- lapse last or never.
/// </remarks>
public static class SlidingSessionModel
{
    public static (int StillAlive, int Expired) SessionsAliveAfter(
        Population population, int users, int timeoutDays, int days, ulong seed)
    {
        var rng = new Pcg32(seed);

        var rates = new double[users];
        var assigned = 0;
        for (var i = 0; i < population.Cohorts.Count; i++)
        {
            var cohort = population.Cohorts[i];
            var count = i == population.Cohorts.Count - 1
                ? users - assigned
                : (int)Math.Round(cohort.Share * users);
            for (var j = 0; j < count && assigned < users; j++, assigned++)
            {
                rates[assigned] = cohort.DailyLoginProbability;
            }
        }

        // Everyone starts holding a valid ticket on the day the cutoff is announced.
        var lastUsed = new int[users];
        var alive = new bool[users];
        Array.Fill(alive, true);

        for (var day = 1; day <= days; day++)
        {
            for (var user = 0; user < users; user++)
            {
                if (!alive[user]) continue;
                if (day - lastUsed[user] > timeoutDays) { alive[user] = false; continue; }
                if (rng.NextBool(rates[user])) lastUsed[user] = day;
            }
        }

        var stillAlive = alive.Count(a => a);
        return (stillAlive, users - stillAlive);
    }
}
