using Microsoft.Data.Sqlite;
using Northstar.Legacy.Web.Legacy;
using Northstar.Legacy.Web.Services;

namespace Northstar.Legacy.CharacterizationTests;

public sealed class LegacyCharacterizationTests
{
    [Fact]
    public void Calculate_NormalLoss_AppliesExcess()
    {
        Assert.Equal(2_900m, LegacySettlementCalculator.Calculate(3_400m, 500m, 10_000m));
    }

    [Fact]
    public void Calculate_LossOverLimit_CapsPayment()
    {
        Assert.Equal(5_000m, LegacySettlementCalculator.Calculate(7_200m, 250m, 5_000m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Calculate_InvalidClaimValue_QuietlyReturnsZero(decimal claimValue)
    {
        Assert.Equal(0m, LegacySettlementCalculator.Calculate(claimValue, 100m, 1_000m));
    }

    [Fact]
    public void Calculate_NegativeDeductible_TreatsItAsZero()
    {
        Assert.Equal(100m, LegacySettlementCalculator.Calculate(100m, -10m, 1_000m));
    }

    [Fact]
    public void FindClaimsByPolicyholderUnsafe_InjectedPredicate_ReturnsAllClaims()
    {
        using var database = LegacyDatabase.CreateSeeded();

        var expectedCount = DatabaseHelper.GetClaims(database.ConnectionString).Count;
        var injected = DatabaseHelper.FindClaimsByPolicyholderUnsafe(
            database.ConnectionString,
            "does-not-exist' OR 1=1 -- ");

        Assert.Equal(expectedCount, injected.Count);
        Assert.True(injected.Count > 1);
    }

    [Fact]
    public void GetClaims_UsesStaticCache_UntilWriterInvalidatesIt()
    {
        using var database = LegacyDatabase.CreateSeeded();

        var firstRead = DatabaseHelper.GetClaims(database.ConnectionString);
        DatabaseHelper.UpdateAssessment(database.ConnectionString, firstRead[0].Id, "A. Adjuster", 1_234m, "UnderReview");
        var secondRead = DatabaseHelper.GetClaims(database.ConnectionString);

        Assert.Equal("A. Adjuster", secondRead[0].Adjuster);
    }

    private sealed class LegacyDatabase : IDisposable
    {
        private readonly SqliteConnection _keeper;

        private LegacyDatabase(SqliteConnection keeper, string connectionString)
        {
            _keeper = keeper;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static LegacyDatabase CreateSeeded()
        {
            var connectionString = $"Data Source=file:legacy-{Guid.NewGuid():N}?mode=memory&cache=shared";
            var keeper = new SqliteConnection(connectionString);
            keeper.Open();
            DatabaseHelper.InitializeSchema(connectionString);
            DatabaseHelper.SeedDemoData(connectionString);
            DatabaseHelper.ClearCache();
            return new LegacyDatabase(keeper, connectionString);
        }

        public void Dispose()
        {
            DatabaseHelper.ClearCache();
            _keeper.Dispose();
        }
    }
}
