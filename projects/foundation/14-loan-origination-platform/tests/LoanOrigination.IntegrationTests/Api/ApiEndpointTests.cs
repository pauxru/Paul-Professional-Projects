using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoanOrigination.Api.Configuration;

namespace LoanOrigination.IntegrationTests.Api;

public sealed class ApiEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task GetLiveHealth_WithoutAuthentication_ReturnsHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostApplication_WithoutToken_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/applications", new
        {
            customerId = Guid.NewGuid(),
            productCode = "SME-FLEX",
            productVersion = 1,
            requestedPrincipal = 100000m,
            requestedTermMonths = 12,
            currency = "KES"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostProduct_WithApplyScopeOnly_Returns403()
    {
        using var client = await factory.CreateAuthorizedClientAsync(ScopePolicies.Apply);

        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            productCode = "NO-AUTH",
            name = "Not allowed",
            minimumPrincipal = 1000m,
            maximumPrincipal = 10000m,
            minimumTermMonths = 1,
            maximumTermMonths = 12,
            annualInterestRate = 12m,
            interestMethod = "ReducingBalance",
            currency = "KES",
            rulesetId = "SME-CREDIT-POLICY",
            rulesetVersion = 1,
            collateralRequired = false
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostCustomer_InvalidPayload_ReturnsProblemDetailsValidationError()
    {
        using var client = await factory.CreateAuthorizedClientAsync(ScopePolicies.Apply);

        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            kind = "Individual",
            legalName = "",
            dateOfBirth = "1990-01-01",
            syntheticIdentityNumber = "synthetic-pass",
            email = "bad@example.test",
            phone = "+254700000001",
            address = "Synthetic",
            monthlyNetIncome = 1000m,
            monthlyExpenses = 100m,
            incomeStabilityMonths = 1,
            incomeSource = "Salary",
            dependants = 0
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task LoanLifecycle_SeededApplicant_ProgressesToDisbursedWithExplainableRecord()
    {
        using var client = await factory.CreateAuthorizedClientAsync(
            ScopePolicies.Apply,
            ScopePolicies.Underwrite,
            ScopePolicies.Approve,
            ScopePolicies.Admin);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "integration-lifecycle");
        var customerId = await GetFirstIdAsync(client, "/api/v1/customers");

        var create = await client.PostAsJsonAsync("/api/v1/applications", new
        {
            customerId,
            productCode = "SME-FLEX",
            productVersion = 1,
            requestedPrincipal = 100000m,
            requestedTermMonths = 12,
            currency = "KES"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal("integration-lifecycle", create.Headers.GetValues("X-Correlation-Id").Single());
        var applicationId = await GetIdAsync(create);

        var submitted = await client.PostAsync($"/api/v1/applications/{applicationId}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        foreach (var documentType in new[] { "NationalId", "BankStatement" })
        {
            var upload = await client.PostAsJsonAsync($"/api/v1/applications/{applicationId}/documents", new
            {
                documentType,
                fileName = $"{documentType}.pdf",
                contentType = "application/pdf",
                base64Content = Convert.ToBase64String("synthetic document"u8.ToArray())
            });
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            using var uploadJson = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
            var documentId = uploadJson.RootElement.GetProperty("documents").EnumerateArray().Last().GetProperty("id").GetGuid();
            var verified = await client.PostAsJsonAsync(
                $"/api/v1/applications/{applicationId}/documents/{documentId}/verify",
                new { status = "Verified", reason = "Synthetic reviewer verification." });
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/applications/{applicationId}/documents/complete", null)).StatusCode);
        var kyc = await client.PostAsync($"/api/v1/applications/{applicationId}/kyc", null);
        Assert.Equal(HttpStatusCode.OK, kyc.StatusCode);
        var screening = await client.PostAsync($"/api/v1/applications/{applicationId}/decision", null);
        Assert.Equal(HttpStatusCode.OK, screening.StatusCode);
        using (var screeningJson = JsonDocument.Parse(await screening.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Underwriting", screeningJson.RootElement.GetProperty("stage").GetString());
            Assert.NotEmpty(screeningJson.RootElement.GetProperty("decisionTrace").GetProperty("rules").EnumerateArray());
        }

        var decision = await client.PostAsJsonAsync($"/api/v1/underwriting/queue/{applicationId}/decision", new
        {
            decision = "APPROVE",
            approvedPrincipal = 100000m,
            annualRate = 18m,
            termMonths = 12,
            reason = "Trace and scorecard reviewed.",
            decisionMakerRole = "Senior"
        });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        var offerResponse = await client.PostAsJsonAsync("/api/v1/offers", new
        {
            applicationId,
            principal = 100000m,
            annualRate = 18m,
            termMonths = 12,
            validityDays = 7
        });
        Assert.Equal(HttpStatusCode.Created, offerResponse.StatusCode);
        var offerId = await GetIdAsync(offerResponse);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/offers/{offerId}/accept", null)).StatusCode);

        var disbursement = await client.PostAsJsonAsync("/api/v1/disbursements", new
        {
            offerId,
            providerReference = "integration-transfer-001",
            rail = "BankTransfer"
        });
        Assert.Equal(HttpStatusCode.Created, disbursement.StatusCode);
        using var disbursementJson = JsonDocument.Parse(await disbursement.Content.ReadAsStringAsync());
        Assert.Equal("Succeeded", disbursementJson.RootElement.GetProperty("status").GetString());
        Assert.True(disbursementJson.RootElement.GetProperty("isReconciled").GetBoolean());

        var decisionRecord = await client.GetAsync($"/api/v1/applications/{applicationId}/decision-record");
        Assert.Equal(HttpStatusCode.OK, decisionRecord.StatusCode);
        using var recordJson = JsonDocument.Parse(await decisionRecord.Content.ReadAsStringAsync());
        Assert.Equal("SME-CREDIT-POLICY", recordJson.RootElement.GetProperty("rulesetId").GetString());
        Assert.NotEmpty(recordJson.RootElement.GetProperty("decisionTrace").GetProperty("rules").EnumerateArray());
    }

    private static async Task<Guid> GetFirstIdAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync($"{path}?page=1&pageSize=10");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray().First().GetProperty("id").GetGuid();
    }

    private static async Task<Guid> GetIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
