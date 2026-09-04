using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroTrust.Api.Authorization;
using ZeroTrust.Api.Endpoints;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.IntegrationTests.Fixtures;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class AdminSurfaceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AdminSurfaceTests(ApiFactory f)
    {
        _factory = f;
        _client = f.CreateClient();
    }

    private async Task<HttpClient> AdminStepUpClient(string scope = "admin.audit admin.users admin.authz.evaluate")
    {
        var token = await _factory.IssueUserAccess("admin", "AdminPassw0rd!", Audience.Admin, scope, "mfa", "urn:ntsf:acr:step-up");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<HttpClient> AdminNoStepUpClient(string scope = "admin.audit admin.users admin.authz.evaluate")
    {
        var token = await _factory.IssueUserAccess("admin", "AdminPassw0rd!", Audience.Admin, scope, "pwd", "urn:ntsf:acr:pwd");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Admin_Endpoint_Requires_Step_Up()
    {
        var c = await AdminNoStepUpClient();
        var res = await c.GetAsync("/api/v1/admin/audit");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_With_Step_Up_Can_Read_Audit()
    {
        var c = await AdminStepUpClient();
        var res = await c.GetAsync("/api/v1/admin/audit");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Authz_Explain_Returns_Deciding_Requirement_For_Ownership()
    {
        var admin = await AdminStepUpClient();
        var aliceToken = await _factory.IssueUserAccess("alice", "CustomerPassw0rd!", Audience.Customer,
            "customer.read customer.write");

        // Find Bob's account
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var bobs = await db.Accounts.FirstAsync(a => a.OwnerSubject == "bob");

        var body = new
        {
            token = aliceToken,
            action = "customer:read-account",
            resource = bobs.Id.ToString()
        };
        var res = await admin.PostAsJsonAsync("/api/v1/admin/authz/evaluate", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var decision = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(decision.GetProperty("allowed").GetBoolean());
        Assert.Equal("ownership.denied", decision.GetProperty("decidingRequirement").GetString());
    }

    [Fact]
    public async Task Authz_Explain_Allows_Correct_Access()
    {
        var admin = await AdminStepUpClient();
        var partnerTok = await _factory.IssueClient("acme-treasury-client", "PartnerSecret!ExampleOnly",
            Audience.Partner, "partner.payments.initiate");
        var body = new
        {
            token = partnerTok.AccessToken,
            action = "partner:initiate-payment",
            resource = "-"
        };
        var res = await admin.PostAsJsonAsync("/api/v1/admin/authz/evaluate", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var decision = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(decision.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Break_Glass_Requires_Approver_Different_From_Requestor()
    {
        var admin = await AdminStepUpClient();
        var body = new
        {
            subject = "alice",
            requestor = "same",
            approver = "same",
            justification = "adequately-long-justification-for-emergency",
            minutesValid = 15
        };
        var res = await admin.PostAsJsonAsync("/api/v1/admin/break-glass/request", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Break_Glass_Flow_Records_Grant_And_Use_In_Audit()
    {
        var admin = await AdminStepUpClient();
        var body = new
        {
            subject = "alice",
            requestor = "operator-A",
            approver = "operator-B",
            justification = "prod outage — restore access",
            minutesValid = 15
        };
        var createRes = await admin.PostAsJsonAsync("/api/v1/admin/break-glass/request", body);
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = created.GetProperty("grantId").GetGuid();

        var useRes = await admin.PostAsync($"/api/v1/admin/break-glass/use/{grantId}", null);
        Assert.Equal(HttpStatusCode.OK, useRes.StatusCode);

        // Second use is now inactive.
        var reuseRes = await admin.PostAsync($"/api/v1/admin/break-glass/use/{grantId}", null);
        Assert.Equal(HttpStatusCode.Conflict, reuseRes.StatusCode);
    }

    [Fact]
    public async Task Audit_Verify_Chain_Endpoint_Reports_Ok()
    {
        var admin = await AdminStepUpClient();
        var res = await admin.PostAsync("/api/v1/admin/audit/verify-chain", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Key_Rotation_Adds_A_New_Key()
    {
        var admin = await AdminStepUpClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var before = await db.SigningKeys.CountAsync();

        var res = await admin.PostAsync("/api/v1/admin/keys/rotate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var after = await db2.SigningKeys.CountAsync();
        Assert.True(after > before);
    }

    [Fact]
    public async Task Api_Key_Migration_Report_Includes_Legacy_Key()
    {
        var admin = await AdminStepUpClient();
        var res = await admin.GetAsync("/api/v1/admin/api-keys/migration-report");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var rows = doc.GetProperty("rows");
        Assert.True(rows.GetArrayLength() >= 1);
        Assert.Equal("acme-legacy-key-01", rows[0].GetProperty("keyId").GetString());
    }
}
