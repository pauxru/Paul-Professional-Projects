using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Models;
using CloudCostObservability.Infrastructure.Ingestion;
using CloudCostObservability.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CloudCostObservability.IntegrationTests;

public sealed class ApiEndpointAndIngestionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory = factory;

    [Fact]
    public async Task HealthReady_WithInMemorySqlite_ReturnsHealthy()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Resources_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/resources");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Resources_WithReadScope_ReturnsPagedResourcesAndCorrelationId()
    {
        using var client = await AuthorizedClientAsync("finops:read");
        var response = await client.GetAsync("/api/v1/resources?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.True(json.RootElement.GetProperty("totalCount").GetInt32() >= 2);
    }

    [Fact]
    public async Task BudgetCreate_WithReadOnlyScope_Returns403()
    {
        using var client = await AuthorizedClientAsync("finops:read");
        var response = await client.PostAsJsonAsync("/api/v1/budgets", new { id = "budget-forbidden", scope = "Team", selector = "commerce", amount = 100, periodStart = "2026-02-01", periodEnd = "2026-02-28" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BudgetCreate_WithInvalidPayload_ReturnsProblemDetails()
    {
        using var client = await AuthorizedClientAsync("finops:manage");
        var response = await client.PostAsJsonAsync("/api/v1/budgets", new { id = "", scope = "Team", selector = "", amount = 0, periodStart = "2026-02-28", periodEnd = "2026-02-01" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("errors", out _));
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Costs_TeamScopedToken_CannotUseQueryToReadAnotherTeam()
    {
        using var client = await AuthorizedClientAsync("finops:read", "commerce");
        var response = await client.GetAsync("/api/v1/costs?from=2026-02-01&to=2026-02-02&groupBy=team&team=data");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("commerce", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"data\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Costs_KesReportingCurrency_UsesFxTable()
    {
        await AddFxRateAsync(130m);
        using var client = await AuthorizedClientAsync("finops:read", "commerce");
        var response = await client.GetAsync("/api/v1/costs?from=2026-02-01&to=2026-02-02&groupBy=team&reportingCurrency=KES");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("KES", json.RootElement.GetProperty("currency").GetString());
        Assert.Equal(27_300m, json.RootElement.GetProperty("totalAmortizedCost").GetDecimal());
    }

    [Fact]
    public async Task Allocations_DirectTagRule_ReturnsReconciledAuditLines()
    {
        using var client = await AuthorizedClientAsync("finops:read");
        var response = await client.GetAsync("/api/v1/allocations?from=2026-02-01&to=2026-02-02&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("invariantHolds").GetBoolean());
        Assert.Equal(620m, json.RootElement.GetProperty("sourceTotal").GetDecimal());
    }

    [Fact]
    public async Task Import_IdempotentThenRestatedDay_CorrectsInsteadOfDuplicating()
    {
        var source = new MemorySource([Line(10m)]);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFinOpsService>();
        var first = await service.ImportAsync(source);
        var restated = await service.ImportAsync(new MemorySource([Line(17m)]));
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        var stored = db.Costs.Single(cost => cost.ResourceId == "commerce-vm" && cost.UsageDate == new DateOnly(2026, 2, 3));
        Assert.Equal(1, first.Inserted);
        Assert.Equal(1, restated.Restated);
        Assert.Equal(17m, stored.AmortizedCost);
        Assert.Equal(1, db.Costs.Count(cost => cost.ResourceId == "commerce-vm" && cost.UsageDate == new DateOnly(2026, 2, 3)));
    }

    [Fact]
    public async Task Import_UnknownResource_IsRejectedThenSucceedsAfterInventoryRecovery()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFinOpsService>();
        var rejected = await service.ImportAsync(new MemorySource([Line(9m, "late-resource")]));
        Assert.Equal(1, rejected.Rejected);
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        db.Resources.Add(new CloudResource("late-resource", "late-resource", "type", ResourceCategory.Compute, "westeurope", "sub-1", "late-rg", "D2", new DateOnly(2026, 1, 1), new Dictionary<string, string> { ["team"] = "commerce" }));
        await db.SaveChangesAsync();
        var recovered = await service.ImportAsync(new MemorySource([Line(9m, "late-resource")]));
        Assert.Equal(1, recovered.Inserted);
    }

    [Fact]
    public async Task AzureExportAdapter_MapsRepresentativeCostManagementColumns()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"azure-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "Date,ResourceId,MeterName,ServiceName,Quantity,UnitOfMeasure,EffectivePrice,CostInBillingCurrency,AmortizedCost,BillingCurrency,ReservationId\n2026-02-03,commerce-vm,VM Hours,Virtual Machines,4,hour,2.5,10,9,USD,res-1");
            var line = await FirstAsync(new AzureCostManagementCsvDataSource(path).ReadAsync());
            Assert.Equal("commerce-vm", line.ResourceId);
            Assert.Equal("VM Hours", line.Meter);
            Assert.Equal(10m, line.ActualCost);
            Assert.Equal(9m, line.AmortizedCost);
            Assert.Equal(100m, line.ReservedCoveragePercent);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task AwsCurAdapter_MapsRepresentativeCurColumns()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"aws-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "lineItem/UsageStartDate,lineItem/ResourceId,lineItem/UsageType,product/ProductName,lineItem/UsageAmount,pricing/publicOnDemandRate,lineItem/UnblendedCost,reservation/EffectiveCost,lineItem/CurrencyCode,reservation/ReservationARN\n2026-02-03T11:00:00Z,commerce-vm,BoxUsage:t3,Amazon Elastic Compute Cloud,4,3,12,9,USD,arn:reservation");
            var line = await FirstAsync(new AwsCurCsvDataSource(path).ReadAsync());
            Assert.Equal("commerce-vm", line.ResourceId);
            Assert.Equal(12m, line.ActualCost);
            Assert.Equal(9m, line.AmortizedCost);
            Assert.True(line.IsHourly);
            Assert.Equal(11, line.Hour);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RecommendationActions_AcceptImplementVerify_UsesLifecycleEndpoint()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        if (!db.Recommendations.Any(item => item.Id == "api-rec"))
        {
            db.Recommendations.Add(new Recommendation("api-rec", RecommendationType.IdleCompute, "commerce-vm", "idle", 25m, ConfidenceLevel.High, "evidence", "steps"));
            await db.SaveChangesAsync();
        }
        using var client = await AuthorizedClientAsync("finops:manage");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/recommendations/api-rec/actions", new { action = "accept" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/recommendations/api-rec/actions", new { action = "implement", baselineMonthlyCost = 100 })).StatusCode);
        var verify = await client.PostAsJsonAsync("/api/v1/recommendations/api-rec/actions", new { action = "verify", postChangeMonthlyCost = 60 });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var body = await verify.Content.ReadAsStringAsync();
        Assert.Contains("Verified", body);
        Assert.Contains("40", body);
    }

    private async Task<HttpClient> AuthorizedClientAsync(string scope, string? team = null)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { subject = "integration-test", scope, team });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private async Task AddFxRateAsync(decimal rate)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        var existing = await db.FxRates.FindAsync("20260215|USD|KES");
        if (existing is null)
        {
            db.FxRates.Add(new FxRate(new DateOnly(2026, 2, 15), "USD", "KES", rate));
            await db.SaveChangesAsync();
        }
    }

    private static CostImportLine Line(decimal amount, string resourceId = "commerce-vm") =>
        new("2026-02", resourceId, new DateOnly(2026, 2, 3), "restatable-meter", "Compute", ResourceCategory.Compute, 1m, "hour", amount, amount, amount, 0m, 0m, 0m, 0m);

    private static async Task<CostImportLine> FirstAsync(IAsyncEnumerable<CostImportLine> source)
    {
        await foreach (var item in source) return item;
        throw new InvalidOperationException("Source was empty.");
    }

    private sealed class MemorySource(IReadOnlyList<CostImportLine> values) : ICostDataSource
    {
        public ImportProvider Provider => ImportProvider.Synthetic;
        public async IAsyncEnumerable<CostImportLine> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }
    }
}

