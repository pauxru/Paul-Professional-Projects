using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Reconciliation;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Payments;
using Contoso.Payments.Infrastructure.Persistence;
using Contoso.Payments.IntegrationTests.Fixture;

namespace Contoso.Payments.IntegrationTests.Endpoints;

public class ReconciliationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ReconciliationTests(ApiFactory f) { _factory = f; }

    private async Task<PaymentIntent> SeedCapturedIntent(string providerRef, decimal amount = 10m, string currency = "USD")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var intent = new PaymentIntent(ids.NewGuid(), ids.NewGuid(), Money.Of(amount, currency), Guid.NewGuid().ToString(), clock.UtcNow);
        intent.MarkAuthorized(providerRef, clock.UtcNow);
        intent.MarkCaptured(clock.UtcNow);
        db.PaymentIntents.Add(intent);
        await db.SaveChangesAsync();
        return intent;
    }

    private async Task<HttpResponseMessage> PostCsv(string csv)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueToken("reconciliation:run"));
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reconciliation/runs")
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv")
        };
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task Clean_file_produces_all_matched()
    {
        var intent = await SeedCapturedIntent("prov-clean-1");
        var rows = new List<SettlementRow>
        {
            new("prov-clean-1", intent.Id, intent.CapturedAmount.ToMinorUnits(), "USD", "Captured")
        };
        var csv = SettlementFileGenerator.Build(rows, MismatchFlags.None);
        var resp = await PostCsv(csv);
        Assert.True(resp.IsSuccessStatusCode);
        var run = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(run.GetProperty("matchedCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Amount_mismatch_flag_produces_mismatch_row()
    {
        var intent = await SeedCapturedIntent("prov-mm-1", 20m);
        var rows = new List<SettlementRow>
        {
            new("prov-mm-0", Guid.NewGuid(), 100L, "USD", "Captured"),
            new("prov-mm-1", intent.Id, intent.CapturedAmount.ToMinorUnits(), "USD", "Captured")
        };
        var csv = SettlementFileGenerator.Build(rows, MismatchFlags.AmountMismatch);
        var resp = await PostCsv(csv);
        Assert.True(resp.IsSuccessStatusCode);
        var run = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(run.GetProperty("amountMismatchCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Missing_internally_flag_produces_row()
    {
        var rows = new List<SettlementRow>(); // no matching internal intents
        var csv = SettlementFileGenerator.Build(rows, MismatchFlags.MissingInternally);
        var resp = await PostCsv(csv);
        Assert.True(resp.IsSuccessStatusCode);
        var run = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(run.GetProperty("missingInternallyCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Missing_in_provider_flag_produces_row()
    {
        var intent = await SeedCapturedIntent("prov-mp-1", 15m);
        // Baseline has one row that will be dropped by the MissingInProvider flag.
        var rows = new List<SettlementRow>
        {
            new("prov-mp-1", intent.Id, intent.CapturedAmount.ToMinorUnits(), "USD", "Captured")
        };
        var csv = SettlementFileGenerator.Build(rows, MismatchFlags.MissingInProvider);
        var resp = await PostCsv(csv);
        Assert.True(resp.IsSuccessStatusCode);
        var run = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(run.GetProperty("missingInProviderCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Duplicate_flag_produces_row()
    {
        var intent = await SeedCapturedIntent("prov-dup-1", 12m);
        var rows = new List<SettlementRow>
        {
            new("prov-dup-1", intent.Id, intent.CapturedAmount.ToMinorUnits(), "USD", "Captured")
        };
        var csv = SettlementFileGenerator.Build(rows, MismatchFlags.DuplicateInProvider);
        var resp = await PostCsv(csv);
        Assert.True(resp.IsSuccessStatusCode);
        var run = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(run.GetProperty("duplicateCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Status_mismatch_flag_produces_row()
    {
        var intent = await SeedCapturedIntent("prov-sm-1", 8m);
        var rows = new List<SettlementRow>
        {
            new("prov-other", Guid.NewGuid(), 100L, "USD", "Captured"),
            new("prov-sm-1", intent.Id, intent.CapturedAmount.ToMinorUnits(), "USD", "Captured")
        };
        var csv = SettlementFileGenerator.Build(rows, MismatchFlags.StatusMismatch);
        var resp = await PostCsv(csv);
        Assert.True(resp.IsSuccessStatusCode);
        var run = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(run.GetProperty("statusMismatchCount").GetInt32() >= 1);
    }
}
