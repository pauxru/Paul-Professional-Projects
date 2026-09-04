using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExampleBank.Ledger.Application.Accounts;
using ExampleBank.Ledger.Application.Common;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExampleBank.Ledger.IntegrationTests;

/// <summary>Convenience helpers for driving the ledger API over HTTP in integration tests.</summary>
public static class LedgerTestApi
{
    public static readonly string[] AllScopes =
        { "ledger:read", "ledger:post", "ledger:adjust", "ledger:admin" };

    public const string IdempotencyHeaderName = "Idempotency-Key";

    public static async Task<string> MintTokenAsync(HttpClient client, params string[] scopes)
    {
        var response = await client.PostAsJsonAsync("/api/v1/dev/token",
            new { subject = "integration-test", scopes = scopes.Length == 0 ? AllScopes : scopes });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    public static HttpClient ClientWithToken(this WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<HttpClient> AdminClientAsync(this WebApplicationFactory<Program> factory)
    {
        var anon = factory.CreateClient();
        var token = await MintTokenAsync(anon, AllScopes);
        return factory.ClientWithToken(token);
    }

    public static async Task<AccountDto> CreateCustomerAccountAsync(
        HttpClient admin, string currency, long overdraftLimitMinor = 0)
    {
        var code = $"CUST-{Guid.NewGuid():N}"[..16];
        var response = await admin.PostAsJsonAsync("/api/v1/accounts", new
        {
            code,
            name = code,
            type = "Liability",
            currency,
            parentCode = $"DEPOSITS-{currency}",
            isCustomerAccount = true,
            overdraftLimitMinor,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    public static async Task<AccountDto> GetAccountByCodeAsync(HttpClient client, string code)
    {
        var accounts = await client.GetFromJsonAsync<List<AccountDto>>("/api/v1/accounts");
        return accounts!.Single(a => a.Code == code);
    }

    public static async Task<AccountDto> GetAccountAsync(HttpClient client, Guid id)
        => (await client.GetFromJsonAsync<AccountDto>($"/api/v1/accounts/{id}"))!;

    /// <summary>Funds a customer deposit account by posting Debit CASH / Credit customer.</summary>
    public static async Task<EntryResult> FundAsync(HttpClient admin, AccountDto customer, long amountMinor)
    {
        var cash = await GetAccountByCodeAsync(admin, $"CASH-{customer.Currency}");
        var response = await admin.PostAsJsonAsync("/api/v1/entries", new
        {
            type = "Adjustment",
            description = "Fund test account",
            postings = new object[]
            {
                new { accountId = cash.Id, direction = "Debit", amountMinor, currency = customer.Currency },
                new { accountId = customer.Id, direction = "Credit", amountMinor, currency = customer.Currency },
            },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EntryResult>())!;
    }

    public static async Task<TrialBalanceResult> GetTrialBalanceAsync(HttpClient client)
        => (await client.GetFromJsonAsync<TrialBalanceResult>("/api/v1/reports/trial-balance"))!;

    public static async Task<IntegrityReport> VerifyIntegrityAsync(HttpClient client)
        => (await client.GetFromJsonAsync<IntegrityReport>("/api/v1/admin/integrity/verify"))!;
}
