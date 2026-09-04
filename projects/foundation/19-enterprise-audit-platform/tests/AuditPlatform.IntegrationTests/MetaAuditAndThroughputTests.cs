using System.Diagnostics;
using System.Net.Http.Json;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Security;
using AuditPlatform.Application.Verification;
using AuditPlatform.Domain.Events;
using AuditPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AuditPlatform.IntegrationTests;

public class MetaAuditAndThroughputTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly ITestOutputHelper _out;
    public MetaAuditAndThroughputTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public async Task Reading_AuditLog_ProducesMetaAuditEvent_WithoutInfiniteRecursion()
    {
        var admin = _factory.CreateAuthClient(tenantId: "meta-tenant");
        await TestData.RegisterUserLoginSchema(admin);
        await admin.PostAsJsonAsync("/api/v1/schemas", new
        {
            EventType = "audit.log.read",
            SchemaJson = "{ \"fields\": [ {\"name\":\"itemsReturned\",\"kind\":\"integer\",\"required\":false} ] }",
            Description = "Audit log read (meta)"
        });
        for (var i = 0; i < 2; i++)
            await admin.PostAsJsonAsync("/api/v1/events", TestData.SampleIngestBody(clientEventId: $"meta-{i}"));

        // First read.
        var listResp = await admin.GetAsync("/api/v1/events?pageSize=10");
        listResp.EnsureSuccessStatusCode();

        // A second read should exist, and both meta-reads should now be present. Loop prevention:
        // the meta-audit ingest path uses ReaderContext.IsMetaAuditor to skip emitting again,
        // avoiding an unbounded recursion.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metaCount = db.Events.Where(e => e.TenantId == "meta-tenant" && e.EventType == "audit.log.read").Count();
        Assert.True(metaCount >= 1);
        // Confirm bounded: not thousands.
        Assert.True(metaCount < 10, $"meta-audit recursion suspected: {metaCount}");
    }

    [Fact]
    public async Task Throughput_Ingests_TwentyThousandEvents_WithinBudget()
    {
        var admin = _factory.CreateAuthClient(tenantId: "throughput");
        await TestData.RegisterUserLoginSchema(admin);

        const int total = 20_000;
        const int batchSize = 500;
        var sw = Stopwatch.StartNew();
        var idx = 0;
        while (idx < total)
        {
            var take = Math.Min(batchSize, total - idx);
            var events = new List<object>(take);
            for (var i = 0; i < take; i++, idx++)
            {
                events.Add(TestData.SampleIngestBody(clientEventId: $"tp-{idx}"));
            }
            var response = await admin.PostAsJsonAsync("/api/v1/events/batch", new { Events = events });
            response.EnsureSuccessStatusCode();
        }
        sw.Stop();
        _out.WriteLine($"Ingested {total} events in {sw.ElapsedMilliseconds} ms ({total * 1000.0 / sw.ElapsedMilliseconds:F0} evt/s)");
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 240);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = db.Events.Count(e => e.TenantId == "throughput");
        Assert.Equal(total, count);
    }
}
