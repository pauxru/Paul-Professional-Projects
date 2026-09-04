using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using ExampleBank.Ledger.Application.Common;

namespace ExampleBank.Ledger.IntegrationTests;

/// <summary>
/// The headline reliability proof for a ledger: money is conserved and never created under real
/// parallel contention. Each test fires 50–200 concurrent operations against a file-backed SQLite
/// database through the full HTTP + concurrency-control stack.
/// </summary>
public sealed class ConcurrencyTests : IClassFixture<LedgerApiFactory>
{
    private readonly LedgerApiFactory _factory;

    public ConcurrencyTests(LedgerApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ParallelTransfers_SameTwoAccounts_ConserveTotalAndDeriveCorrectly()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 1_000_000);

        const int operations = 150;
        const long amount = 500;

        var responses = await Task.WhenAll(Enumerable.Range(0, operations).Select(_ =>
            PostTransferAsync(admin, a.Id, b.Id, amount, Guid.NewGuid().ToString("n"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var aAfter = await LedgerTestApi.GetAccountAsync(admin, a.Id);
        var bAfter = await LedgerTestApi.GetAccountAsync(admin, b.Id);

        Assert.Equal(1_000_000 - operations * amount, aAfter.BalanceMinor);
        Assert.Equal(operations * amount, bAfter.BalanceMinor);
        Assert.Equal(1_000_000, aAfter.BalanceMinor + bAfter.BalanceMinor); // total conserved

        // Cached balances must reconcile exactly with the balances derived from the journal.
        var integrity = await LedgerTestApi.VerifyIntegrityAsync(admin);
        Assert.True(integrity.IsHealthy);
    }

    [Fact]
    public async Task BidirectionalTransfers_DoNotDeadlock_AndConserveTotal()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 500_000);
        await LedgerTestApi.FundAsync(admin, b, 500_000);

        const int eachDirection = 75; // 150 operations, half A->B and half B->A
        const long amount = 1_000;

        var aToB = Enumerable.Range(0, eachDirection).Select(_ =>
            PostTransferAsync(admin, a.Id, b.Id, amount, Guid.NewGuid().ToString("n")));
        var bToA = Enumerable.Range(0, eachDirection).Select(_ =>
            PostTransferAsync(admin, b.Id, a.Id, amount, Guid.NewGuid().ToString("n")));

        var stopwatch = Stopwatch.StartNew();
        // Ordered per-account locking must prevent the classic A<->B deadlock; completion proves it.
        var responses = await Task.WhenAll(aToB.Concat(bToA));
        stopwatch.Stop();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), "Transfers appear to have deadlocked.");

        var aAfter = await LedgerTestApi.GetAccountAsync(admin, a.Id);
        var bAfter = await LedgerTestApi.GetAccountAsync(admin, b.Id);
        Assert.Equal(1_000_000, aAfter.BalanceMinor + bAfter.BalanceMinor); // symmetric flows net out
    }

    [Fact]
    public async Task ConcurrentWithdrawals_AgainstLimitedFunds_NeverOverdraw()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES", overdraftLimitMinor: 0);
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);

        const int operations = 50;
        const long amount = 10_000; // demand 500,000 >> 100,000 available; only 10 can succeed

        var responses = await Task.WhenAll(Enumerable.Range(0, operations).Select(_ =>
            PostTransferAsync(admin, a.Id, b.Id, amount, Guid.NewGuid().ToString("n"))));

        int succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        int rejected = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(10, succeeded);
        Assert.Equal(operations - 10, rejected);

        var aAfter = await LedgerTestApi.GetAccountAsync(admin, a.Id);
        var bAfter = await LedgerTestApi.GetAccountAsync(admin, b.Id);
        Assert.True(aAfter.BalanceMinor >= 0, "Source account was overdrawn.");
        Assert.Equal(0, aAfter.BalanceMinor);
        Assert.Equal(100_000, bAfter.BalanceMinor);
    }

    [Fact]
    public async Task ParallelDuplicateIdempotencyKeys_ProduceExactlyOnePosting()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);

        const int operations = 50;
        var sharedKey = Guid.NewGuid().ToString("n");

        var responses = await Task.WhenAll(Enumerable.Range(0, operations).Select(_ =>
            PostTransferAsync(admin, a.Id, b.Id, 5_000, sharedKey)));

        var entryIds = new ConcurrentBag<Guid>();
        foreach (var response in responses)
        {
            if (response.IsSuccessStatusCode)
            {
                var entry = (await response.Content.ReadFromJsonAsync<EntryResult>())!;
                entryIds.Add(entry.Id);
            }
        }

        // Every successful caller must observe the very same single entry.
        Assert.Single(entryIds.Distinct());

        // And the effect must have been applied exactly once, not 50 times.
        var aAfter = await LedgerTestApi.GetAccountAsync(admin, a.Id);
        Assert.Equal(95_000, aAfter.BalanceMinor);
    }

    private static Task<HttpResponseMessage> PostTransferAsync(
        HttpClient client, Guid from, Guid to, long amountMinor, string idempotencyKey, string currency = "KES")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new
            {
                fromAccountId = from,
                toAccountId = to,
                amountMinor,
                currency,
                description = "concurrent-transfer",
            }),
        };
        request.Headers.Add(LedgerTestApi.IdempotencyHeaderName, idempotencyKey);
        return client.SendAsync(request);
    }
}
