using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Domain;
using Contoso.Storefront.Infrastructure.Messaging;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Contoso.Storefront.UnitTests;

public sealed class MigrationAndWorkerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MigrationRunner_AllMigrationsApplied_HasNoPendingMigrations()
    {
        await using var database = await TestDatabase.CreateAsync();

        await database.Context.Database.MigrateAsync();

        Assert.Empty(await database.Context.Database.GetPendingMigrationsAsync());
        Assert.Equal(6, (await database.Context.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task MigrationRunner_RepeatedRun_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Context.Database.MigrateAsync();
        var first = (await database.Context.Database.GetAppliedMigrationsAsync()).ToArray();

        await database.Context.Database.MigrateAsync();
        var second = (await database.Context.Database.GetAppliedMigrationsAsync()).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ExpandContractMigrations_ApplyColumnsInSafeOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var migrator = database.Context.GetService<IMigrator>();

        await migrator.MigrateAsync("20260903010000_InitialSchema");
        Assert.Contains("description", await ColumnNamesAsync(database.Connection, "products"));
        Assert.DoesNotContain("description_v2", await ColumnNamesAsync(database.Connection, "products"));

        await migrator.MigrateAsync("20260903011000_ExpandProductDescription");
        var expanded = await ColumnNamesAsync(database.Connection, "products");
        Assert.Contains("description", expanded);
        Assert.Contains("description_v2", expanded);

        await migrator.MigrateAsync("20260903015000_ContractProductDescription");
        var contracted = await ColumnNamesAsync(database.Connection, "products");
        Assert.DoesNotContain("description", contracted);
        Assert.Contains("description_v2", contracted);
    }

    [Fact]
    public async Task BackfillMigration_CopiesLegacyDescriptionBeforeReadSwitch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var migrator = database.Context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260903010000_InitialSchema");
        await database.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO products
                 (id, sku, name, description, price_amount, currency, is_active, created_at, version)
             VALUES
                 ({Guid.NewGuid()}, {"LEGACY-1"}, {"Legacy"}, {"legacy description"}, {10m}, {"USD"}, {true}, {Now}, {0});
             """);

        await migrator.MigrateAsync("20260903012000_BackfillProductDescription");

        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT description_v2 FROM products LIMIT 1";
        var description = (string?)await command.ExecuteScalarAsync();
        Assert.Equal("legacy description", description);
    }

    [Fact]
    public async Task OutboxProcessor_OnSuccess_MarksMessageAndAdvancesCheckpoint()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var message = new OutboxMessage(Guid.NewGuid(), "event.v1", "{}", Now);
        database.Context.OutboxMessages.Add(message);
        await database.Context.SaveChangesAsync();
        var transport = new RecordingTransport();
        var processor = new OutboxProcessor(database.Context, transport, new FakeClock());

        var count = await processor.ProcessOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Single(transport.Messages);
        Assert.NotNull(message.ProcessedAt);
        var checkpoint = await database.Context.WorkerCheckpoints.SingleAsync();
        Assert.Equal(message.Id, checkpoint.LastMessageId);
    }

    [Fact]
    public async Task OutboxProcessor_WhenCancelledDuringPublish_DoesNotCheckpointOrLoseMessage()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        var message = new OutboxMessage(Guid.NewGuid(), "event.v1", "{}", Now);
        database.Context.OutboxMessages.Add(message);
        await database.Context.SaveChangesAsync();
        var transport = new BlockingTransport();
        var processor = new OutboxProcessor(database.Context, transport, new FakeClock());
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessOnceAsync(10, cancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.OutboxMessages.SingleAsync();
        Assert.Null(persisted.ProcessedAt);
        Assert.Empty(await database.Context.WorkerCheckpoints.ToListAsync());
    }

    [Fact]
    public async Task OutboxProcessor_WhenTransportFails_RecordsAttemptAndLeavesPending()
    {
        await using var database = await TestDatabase.CreateMigratedAsync();
        database.Context.OutboxMessages.Add(
            new OutboxMessage(Guid.NewGuid(), "event.v1", "{}", Now));
        await database.Context.SaveChangesAsync();
        var processor = new OutboxProcessor(
            database.Context,
            new FailingTransport(),
            new FakeClock());

        var count = await processor.ProcessOnceAsync(10, CancellationToken.None);

        var message = await database.Context.OutboxMessages.SingleAsync();
        Assert.Equal(0, count);
        Assert.Equal(1, message.DeliveryAttempts);
        Assert.Null(message.ProcessedAt);
        Assert.Contains("simulated", message.LastError);
    }

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(SqliteConnection connection, StorefrontDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }
        public StorefrontDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<StorefrontDbContext>()
                .UseSqlite(connection)
                .Options;
            return new TestDatabase(connection, new StorefrontDbContext(options));
        }

        public static async Task<TestDatabase> CreateMigratedAsync()
        {
            var database = await CreateAsync();
            await database.Context.Database.MigrateAsync();
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(1);
    }

    private sealed class RecordingTransport : IOutboxTransport
    {
        public List<(string Type, string Payload)> Messages { get; } = [];

        public Task PublishAsync(
            Guid messageId,
            string type,
            string payload,
            CancellationToken cancellationToken)
        {
            Messages.Add((type, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingTransport : IOutboxTransport
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(
            Guid messageId,
            string type,
            string payload,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingTransport : IOutboxTransport
    {
        public Task PublishAsync(
            Guid messageId,
            string type,
            string payload,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated transport failure");
    }
}
