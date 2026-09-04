using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExampleBank.Ledger.Application.Common;

namespace ExampleBank.Ledger.IntegrationTests;

public sealed class LedgerApiTests : IClassFixture<LedgerApiFactory>
{
    private readonly LedgerApiFactory _factory;

    public LedgerApiTests(LedgerApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_IsAnonymous_AndReportsHealthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadEndpoint_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/reports/trial-balance");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateAccount_WithReadOnlyScope_Returns403()
    {
        var anon = _factory.CreateClient();
        var readToken = await LedgerTestApi.MintTokenAsync(anon, "ledger:read");
        var readClient = _factory.ClientWithToken(readToken);

        var response = await readClient.PostAsJsonAsync("/api/v1/accounts", new
        {
            code = "SHOULD-FAIL",
            name = "x",
            type = "Liability",
            currency = "KES",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_HappyPath_MovesFundsAndSealsHashChain()
    {
        var admin = await _factory.AdminClientAsync();
        var alice = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var bob = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, alice, 100_000);

        var response = await admin.PostAsJsonAsync("/api/v1/transfers", new
        {
            fromAccountId = alice.Id,
            toAccountId = bob.Id,
            amountMinor = 30_000,
            currency = "KES",
            description = "rent",
        });

        response.EnsureSuccessStatusCode();
        var entry = (await response.Content.ReadFromJsonAsync<EntryResult>())!;
        Assert.Equal("Transfer", entry.Type);
        Assert.True(entry.SequenceNumber > 0);
        Assert.False(string.IsNullOrEmpty(entry.Hash));
        Assert.Equal(2, entry.Postings.Count);

        var aliceAfter = await LedgerTestApi.GetAccountAsync(admin, alice.Id);
        var bobAfter = await LedgerTestApi.GetAccountAsync(admin, bob.Id);
        Assert.Equal(70_000, aliceAfter.BalanceMinor);
        Assert.Equal(30_000, bobAfter.BalanceMinor);
    }

    [Fact]
    public async Task Transfer_NegativeAmount_Returns422ProblemDetails()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");

        var response = await admin.PostAsJsonAsync("/api/v1/transfers", new
        {
            fromAccountId = a.Id,
            toAccountId = b.Id,
            amountMinor = -5,
            currency = "KES",
            description = "bad",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_Returns422()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 1_000);

        var response = await admin.PostAsJsonAsync("/api/v1/transfers", new
        {
            fromAccountId = a.Id,
            toAccountId = b.Id,
            amountMinor = 5_000,
            currency = "KES",
            description = "overdraw",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("insufficient_funds", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CloseAccount_WithNonZeroBalance_Returns400()
    {
        var admin = await _factory.AdminClientAsync();
        var account = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, account, 5_000);

        var response = await admin.PostAsync($"/api/v1/accounts/{account.Id}/close", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account.non_zero_balance", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Transfer_RepeatedIdempotencyKey_ReturnsOriginalAndPostsOnce()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);

        var key = Guid.NewGuid().ToString("n");
        var first = await PostTransferWithKeyAsync(admin, a.Id, b.Id, 25_000, key);
        var second = await PostTransferWithKeyAsync(admin, a.Id, b.Id, 25_000, key);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.SequenceNumber, second.SequenceNumber);

        // The transfer must have been applied exactly once.
        var aAfter = await LedgerTestApi.GetAccountAsync(admin, a.Id);
        Assert.Equal(75_000, aAfter.BalanceMinor);
    }

    [Fact]
    public async Task TrialBalance_AcrossActivity_SumsToZeroPerCurrency()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "USD");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "USD");
        await LedgerTestApi.FundAsync(admin, a, 50_000);
        await PostTransferWithKeyAsync(admin, a.Id, b.Id, 20_000, Guid.NewGuid().ToString("n"), "USD");

        var trial = await LedgerTestApi.GetTrialBalanceAsync(admin);

        Assert.True(trial.IsBalanced);
        Assert.All(trial.Totals, t => Assert.Equal(t.TotalDebitsMinor, t.TotalCreditsMinor));
    }

    [Fact]
    public async Task Integrity_AfterActivity_ChainValidAndBalancesReconcile()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 40_000);
        await PostTransferWithKeyAsync(admin, a.Id, b.Id, 10_000, Guid.NewGuid().ToString("n"));

        var report = await LedgerTestApi.VerifyIntegrityAsync(admin);

        Assert.True(report.Chain.IsValid);
        Assert.True(report.Reconciliation.IsReconciled);
        Assert.True(report.IsHealthy);
    }

    [Fact]
    public async Task Statement_TiesOut_OpeningPlusMovementsEqualsClosing()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);
        await PostTransferWithKeyAsync(admin, a.Id, b.Id, 15_000, Guid.NewGuid().ToString("n"));
        await PostTransferWithKeyAsync(admin, a.Id, b.Id, 5_000, Guid.NewGuid().ToString("n"));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = today.AddDays(1).ToString("yyyy-MM-dd");
        var statement = await admin.GetFromJsonAsync<StatementResult>(
            $"/api/v1/statements/{a.Id}?from={from}&to={to}");

        Assert.NotNull(statement);
        Assert.Equal(
            statement!.ClosingBalanceMinor,
            statement.OpeningBalanceMinor + statement.TotalMovements);
    }

    [Fact]
    public async Task Reversal_Full_RestoresBalancesAndReferencesOriginal()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);
        var transfer = await PostTransferWithKeyAsync(admin, a.Id, b.Id, 30_000, Guid.NewGuid().ToString("n"));

        var response = await admin.PostAsJsonAsync("/api/v1/reversals", new { originalEntryId = transfer.Id });
        response.EnsureSuccessStatusCode();
        var reversal = (await response.Content.ReadFromJsonAsync<EntryResult>())!;

        Assert.Equal("Reversal", reversal.Type);
        Assert.Equal(transfer.Id, reversal.ReversalOfEntryId);

        var aAfter = await LedgerTestApi.GetAccountAsync(admin, a.Id);
        var bAfter = await LedgerTestApi.GetAccountAsync(admin, b.Id);
        Assert.Equal(100_000, aAfter.BalanceMinor);
        Assert.Equal(0, bAfter.BalanceMinor);
    }

    [Fact]
    public async Task Reversal_Twice_Returns400_CannotExceedOriginal()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);
        var transfer = await PostTransferWithKeyAsync(admin, a.Id, b.Id, 30_000, Guid.NewGuid().ToString("n"));

        (await admin.PostAsJsonAsync("/api/v1/reversals", new { originalEntryId = transfer.Id }))
            .EnsureSuccessStatusCode();

        var second = await admin.PostAsJsonAsync("/api/v1/reversals", new { originalEntryId = transfer.Id });

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("reversal.exceeds_original", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reversal_Partial_ThenExceedingRemainder_Returns400()
    {
        var admin = await _factory.AdminClientAsync();
        var a = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        var b = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, a, 100_000);
        var transfer = await PostTransferWithKeyAsync(admin, a.Id, b.Id, 10_000, Guid.NewGuid().ToString("n"));

        (await admin.PostAsJsonAsync("/api/v1/reversals",
            new { originalEntryId = transfer.Id, amountMinor = 4_000 })).EnsureSuccessStatusCode();

        // Only 6,000 remains un-reversed; asking for 7,000 must be rejected.
        var exceeding = await admin.PostAsJsonAsync("/api/v1/reversals",
            new { originalEntryId = transfer.Id, amountMinor = 7_000 });

        Assert.Equal(HttpStatusCode.BadRequest, exceeding.StatusCode);

        // The exact remainder is allowed.
        var remainder = await admin.PostAsJsonAsync("/api/v1/reversals",
            new { originalEntryId = transfer.Id, amountMinor = 6_000 });
        remainder.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Fx_Convert_MovesValueAcrossCurrencies_AndKeepsTrialBalanceZero()
    {
        var admin = await _factory.AdminClientAsync();
        var usd = await LedgerTestApi.CreateCustomerAccountAsync(admin, "USD");
        var kes = await LedgerTestApi.CreateCustomerAccountAsync(admin, "KES");
        await LedgerTestApi.FundAsync(admin, usd, 100_000);

        var quoteResponse = await admin.PostAsJsonAsync("/api/v1/fx/quote", new
        {
            fromAccountId = usd.Id,
            toAccountId = kes.Id,
            amountMinor = 10_000,
        });
        quoteResponse.EnsureSuccessStatusCode();
        using var quoteDoc = JsonDocument.Parse(await quoteResponse.Content.ReadAsStringAsync());
        long targetMinor = quoteDoc.RootElement.GetProperty("targetMinor").GetInt64();

        var convertResponse = await admin.PostAsJsonAsync("/api/v1/fx/convert", new
        {
            fromAccountId = usd.Id,
            toAccountId = kes.Id,
            amountMinor = 10_000,
        });
        convertResponse.EnsureSuccessStatusCode();
        var entry = (await convertResponse.Content.ReadFromJsonAsync<EntryResult>())!;
        Assert.Equal("FxConversion", entry.Type);

        var usdAfter = await LedgerTestApi.GetAccountAsync(admin, usd.Id);
        var kesAfter = await LedgerTestApi.GetAccountAsync(admin, kes.Id);
        Assert.Equal(90_000, usdAfter.BalanceMinor);       // gave up exactly the source amount
        Assert.Equal(targetMinor, kesAfter.BalanceMinor);  // received exactly the quoted target

        var trial = await LedgerTestApi.GetTrialBalanceAsync(admin);
        Assert.True(trial.IsBalanced);
    }

    private static async Task<EntryResult> PostTransferWithKeyAsync(
        HttpClient client, Guid from, Guid to, long amountMinor, string idempotencyKey, string currency = "KES")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new
            {
                fromAccountId = from,
                toAccountId = to,
                amountMinor,
                currency,
                description = "transfer",
            }),
        };
        request.Headers.Add(LedgerTestApi.IdempotencyHeaderName, idempotencyKey);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EntryResult>())!;
    }
}
