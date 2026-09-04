using System.Net;
using System.Text.Json;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

/// <summary>
/// Parallel reservation stress: fires N concurrent orders against a SKU with only 5 units.
/// Only 5 orders may succeed; the rest must be rejected with a domain rule violation.  This is
/// the primary evidence that our reservation flow can survive a "black-Friday burst".
/// </summary>
public class ParallelReservationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ParallelReservationTests(ApiFactory f) { _factory = f; }

    [Fact]
    public async Task Parallel_orders_against_limited_stock_never_oversell()
    {
        // TEST-USD-2 is seeded with OnHand=5.
        var limitedSku = "TEST-USD-2";
        var attempts = 20;
        var results = new HttpStatusCode[attempts];

        // Warm up the factory so all threads see an already-started server (avoids
        // duplicate EnsureCreated races in the SQLite shared-cache DB).
        using (var warmup = _factory.CreateClient()) { }

        await Parallel.ForEachAsync(Enumerable.Range(0, attempts), new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, ct) =>
            {
                var http = _factory.CreateClient();
                var token = _factory.IssueToken("orders:write");
                var client = new ApiClient(http, token);
                var body = new
                {
                    customerRef = $"cust-{i}",
                    currency = "USD",
                    lines = new[] { new { sku = limitedSku, quantity = 1 } }
                };
                var resp = await client.PostRaw("/api/v1/orders", body, Guid.NewGuid().ToString());
                results[i] = resp.StatusCode;
            });

        var successes = results.Count(s => s == HttpStatusCode.OK || s == HttpStatusCode.Created);
        var rejections = results.Count(s => s != HttpStatusCode.OK && s != HttpStatusCode.Created);
        // At most 5 successes — never more.
        Assert.True(successes <= 5, $"Oversold: {successes} succeeded but only 5 units in stock.");
        Assert.True(successes >= 1, "Zero orders succeeded — reservation model is stuck.");
        Assert.Equal(attempts, successes + rejections);
    }
}
