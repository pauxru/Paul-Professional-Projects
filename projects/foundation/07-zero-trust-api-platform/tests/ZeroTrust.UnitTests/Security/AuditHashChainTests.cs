using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;
using ZeroTrust.Infrastructure.Time;

namespace ZeroTrust.UnitTests.Security;

public class AuditHashChainTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ZeroTrustDbContext _db = default!;
    private AuditLog _log = default!;
    private FakeClock _clock = default!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var opts = new DbContextOptionsBuilder<ZeroTrustDbContext>()
            .UseSqlite(_connection).Options;
        _db = new ZeroTrustDbContext(opts);
        await _db.Database.EnsureCreatedAsync();
        _clock = new FakeClock(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        _log = new AuditLog(_db, _clock);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Chain_Verifies_When_Untampered()
    {
        for (var i = 0; i < 5; i++)
            await _log.AppendAsync(AuditKind.AuthorizationAllow, "alice", "act", "res", "cid",
                "127.0.0.1", "ua", $"detail-{i}", true, CancellationToken.None);
        var (ok, failing) = await _log.VerifyChainAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Null(failing);
    }

    [Fact]
    public async Task Chain_Detects_Tampering_When_Detail_Modified()
    {
        for (var i = 0; i < 3; i++)
            await _log.AppendAsync(AuditKind.AuthorizationAllow, "alice", "act", "res", "cid",
                "127.0.0.1", "ua", $"detail-{i}", true, CancellationToken.None);

        // Tamper directly via SQL — the application code cannot mutate audit rows.
        await _db.Database.ExecuteSqlRawAsync("UPDATE audit_records SET Detail = 'HACKED' WHERE Sequence = 2;");

        var (ok, failing) = await _log.VerifyChainAsync(CancellationToken.None);
        Assert.False(ok);
        Assert.NotNull(failing);
    }
}
