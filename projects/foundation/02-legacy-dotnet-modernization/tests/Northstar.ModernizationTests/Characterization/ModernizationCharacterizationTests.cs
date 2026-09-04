using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northstar.Application.Abstractions;
using Northstar.Application.Claims;
using Northstar.Application.Importing;
using Northstar.Domain.Claims;
using Northstar.Domain.Policies;
using Northstar.Infrastructure.Importing;
using Northstar.Infrastructure.Persistence;
using Northstar.Legacy.Web.Legacy;
using Northstar.Legacy.Web.Services;

namespace Northstar.ModernizationTests.Characterization;

public sealed class ModernizationCharacterizationTests
{
    public static IEnumerable<object[]> SettlementCases =>
    [
        [3_400m, 500m, 10_000m],
        [7_200m, 250m, 5_000m],
        [100m, 500m, 10_000m],
        [0m, 100m, 1_000m],
        [100m, -10m, 1_000m]
    ];

    [Theory]
    [MemberData(nameof(SettlementCases))]
    public void SettlementCalculator_CharacterizedLegacyInputs_ProducesEquivalentAmount(decimal claimed, decimal deductible, decimal limit)
    {
        var legacy = LegacySettlementCalculator.Calculate(claimed, deductible, limit);
        var modern = new SettlementCalculator().CalculateAmount(claimed, deductible, limit);

        Assert.Equal(legacy, modern);
    }

    [Fact]
    public async Task PolicyholderFiltering_InjectedLegacyTerm_IsNotExploitableInModernEfQuery()
    {
        using var legacy = LegacyDatabase.CreateSeeded();
        await using var modern = await ModernHarness.CreateAsync();
        await modern.AddPolicyAndClaimsAsync();

        var injection = "does-not-exist' OR 1=1 -- ";
        var legacyResults = DatabaseHelper.FindClaimsByPolicyholderUnsafe(legacy.ConnectionString, injection);
        var modernResults = await modern.Store.SearchClaimsAsync(new ClaimSearch(injection, null, 1, 25), CancellationToken.None);

        Assert.True(legacyResults.Count >= 2);
        Assert.Empty(modernResults.Items);
    }

    [Fact]
    public async Task LegacyClaimImporter_SeededSchemaWithBadRow_ImportsValidRowsAndReportsRejection()
    {
        using var legacy = LegacyDatabase.CreateSeeded();
        await legacy.InsertBadRowAsync();
        await using var modern = await ModernHarness.CreateAsync();
        var importer = new LegacyClaimImporter(new LegacySqliteClaimSource(legacy.ConnectionString), modern.Store);

        var report = await importer.ImportAsync(CancellationToken.None);

        Assert.Equal(3, report.SourceRowCount);
        Assert.Equal(2, report.ImportedRowCount);
        var rejection = Assert.Single(report.RejectedRows);
        Assert.Equal("CLM-BAD-001", rejection.ClaimReference);
        Assert.Contains("greater than zero", rejection.Reason);
        Assert.NotEqual(report.SourceChecksum, report.ImportedChecksum);
        var imported = await modern.DbContext.Claims.SingleAsync(claim => claim.Reference == "CLM-1001");
        Assert.Equal(3_400m, imported.ClaimedAmount);
        Assert.Equal(3_400m, imported.ReserveAmount);
        Assert.Equal(2, await modern.DbContext.Policies.CountAsync());
    }

    [Fact]
    public async Task ClaimService_Intake_UsesCharacterizedSettlementForInitialReserve()
    {
        await using var modern = await ModernHarness.CreateAsync();
        var policyholder = new Policyholder(Guid.NewGuid(), "Northstar Logistics (fictional)", "ops@northstar.example");
        var policy = new Policy(Guid.NewGuid(), policyholder.Id, "POL-CHAR-01", 500m, 10_000m, "USD");
        modern.DbContext.AddRange(policyholder, policy);
        await modern.DbContext.SaveChangesAsync();
        var service = new ClaimApplicationService(modern.Store, new InMemoryDocumentStore(), new FixedClock(), new SettlementCalculator());

        var claim = await service.IntakeAsync(new IntakeClaimCommand("POL-CHAR-01", "CLM-CHAR-01", 3_400m, "USD"), CancellationToken.None);

        Assert.Equal(LegacySettlementCalculator.Calculate(3_400m, 500m, 10_000m), claim.ReserveAmount);
        Assert.Equal(2, claim.Version);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class InMemoryDocumentStore : IDocumentStore
    {
        public Task<StoredDocument> SaveAsync(DocumentWrite document, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredDocument($"memory/{document.OriginalName}"));
    }

    private sealed class ModernHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ModernHarness(SqliteConnection connection, NorthstarDbContext dbContext)
        {
            _connection = connection;
            DbContext = dbContext;
            Store = new EfClaimsStore(dbContext);
        }

        public NorthstarDbContext DbContext { get; }
        public EfClaimsStore Store { get; }

        public static async Task<ModernHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new NorthstarDbContext(
                new DbContextOptionsBuilder<NorthstarDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new ModernHarness(connection, context);
        }

        public async Task AddPolicyAndClaimsAsync()
        {
            var holder = new Policyholder(Guid.NewGuid(), "Acme Manufacturing (fictional)", "claims@acme.example");
            var policy = new Policy(Guid.NewGuid(), holder.Id, "POL-EF-1", 500m, 10_000m, "USD");
            var claim = Claim.Create(Guid.NewGuid(), policy.Id, "CLM-EF-1", 1_000m, "USD", DateTimeOffset.UnixEpoch);
            DbContext.AddRange(holder, policy, claim);
            await DbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
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
            var connectionString = $"Data Source=file:shared-legacy-{Guid.NewGuid():N}?mode=memory&cache=shared";
            var keeper = new SqliteConnection(connectionString);
            keeper.Open();
            DatabaseHelper.InitializeSchema(connectionString);
            DatabaseHelper.SeedDemoData(connectionString);
            DatabaseHelper.ClearCache();
            return new LegacyDatabase(keeper, connectionString);
        }

        public async Task InsertBadRowAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Claims (ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, CreatedUtc)
                VALUES ('CLM-BAD-001', 1, 'Acme Manufacturing (fictional)', 'Submitted', -10, 500, 10000, 0, 'USD', @created);
                """;
            command.Parameters.AddWithValue("@created", DateTimeOffset.UnixEpoch.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            DatabaseHelper.ClearCache();
            _keeper.Dispose();
        }
    }
}
