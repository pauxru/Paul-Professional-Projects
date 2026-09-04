using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldOps.UnitTests;

public sealed class TenantPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    [Fact]
    public async Task QueryFilters_ListOnlyAmbientTenantRows()
    {
        await using var connection = await OpenConnectionAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using (var dbA = CreateContext(connection, tenantA))
        {
            await dbA.Database.EnsureCreatedAsync();
            dbA.Jobs.Add(CreateJob(tenantA, "A job"));
            await dbA.SaveChangesAsync();
        }
        await using (var dbB = CreateContext(connection, tenantB))
        {
            dbB.Jobs.Add(CreateJob(tenantB, "B job"));
            await dbB.SaveChangesAsync();
        }
        await using (var dbA = CreateContext(connection, tenantA))
        {
            var jobs = await dbA.Jobs.AsNoTracking().ToListAsync();
            Assert.Single(jobs);
            Assert.Equal(tenantA, jobs[0].TenantId);
        }
    }

    [Fact]
    public async Task Interceptor_EmptyTenantOnInsert_IsStamped()
    {
        await using var connection = await OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        await using var db = CreateContext(connection, tenant);
        await db.Database.EnsureCreatedAsync();
        var job = CreateJob(Guid.Empty, "Stamped");
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        Assert.Equal(tenant, job.TenantId);
    }

    [Fact]
    public async Task Interceptor_ForeignTenantInsert_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        await using var db = CreateContext(connection, tenant);
        await db.Database.EnsureCreatedAsync();
        db.Jobs.Add(CreateJob(Guid.NewGuid(), "Foreign"));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ForgottenFilterPath_ForeignUpdate_IsCaughtByInterceptor()
    {
        await using var connection = await OpenConnectionAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        Guid foreignJobId;
        await using (var dbB = CreateContext(connection, tenantB))
        {
            await dbB.Database.EnsureCreatedAsync();
            var job = CreateJob(tenantB, "Foreign job");
            dbB.Jobs.Add(job);
            await dbB.SaveChangesAsync();
            foreignJobId = job.Id;
        }

        await using (var dbA = CreateContext(connection, tenantA))
        {
            var unsafeRepository = new UnsafeJobRepository(dbA);
            var foreign = await unsafeRepository.FindIgnoringFiltersAsync(foreignJobId, default);
            Assert.NotNull(foreign);
            foreign!.TransitionTo(JobStatus.Scheduled, Now);
            await Assert.ThrowsAsync<CrossTenantAccessException>(() => unsafeRepository.SaveAsync(default));
        }
    }

    [Fact]
    public async Task AuditEntry_Modification_IsRejectedAsAppendOnly()
    {
        await using var connection = await OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        await using var db = CreateContext(connection, tenant);
        await db.Database.EnsureCreatedAsync();
        var audit = new AuditEntry(tenant, "actor", "job.created", "job/1", null, "hash", "corr", null, null, Now);
        db.AuditEntries.Add(audit);
        await db.SaveChangesAsync();
        db.Entry(audit).State = EntityState.Modified;
        await Assert.ThrowsAsync<ForbiddenOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Model_EveryTenantOwnedEntity_HasGlobalQueryFilter()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateContext(connection, Guid.NewGuid());
        await db.Database.EnsureCreatedAsync();
        var unfiltered = db.Model.GetEntityTypes()
            .Where(x => typeof(ITenantOwned).IsAssignableFrom(x.ClrType))
            .Where(x => !x.GetDeclaredQueryFilters().Any())
            .Select(x => x.ClrType.Name)
            .ToArray();
        Assert.Empty(unfiltered);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static FieldOpsDbContext CreateContext(SqliteConnection connection, Guid tenantId)
    {
        var tenant = new FakeTenantContext(tenantId);
        var interceptor = new TenantSaveChangesInterceptor(
            tenant,
            NullLogger<TenantSaveChangesInterceptor>.Instance);
        var options = new DbContextOptionsBuilder<FieldOpsDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        return new FieldOpsDbContext(options, tenant);
    }

    private static Job CreateJob(Guid tenantId, string title) =>
        new(
            tenantId,
            title,
            "Synthetic tenant isolation test",
            JobPriority.Normal,
            Now,
            Now.AddHours(2),
            Now.AddHours(3),
            Now);
}
