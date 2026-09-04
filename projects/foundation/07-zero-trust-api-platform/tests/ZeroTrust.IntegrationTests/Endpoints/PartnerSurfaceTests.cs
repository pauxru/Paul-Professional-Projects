using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.IntegrationTests.Fixtures;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class PartnerSurfaceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private const string ThumbprintAcme = "AA11BB22CC33DD44EE55FF66AA11BB22CC33DD44";

    public PartnerSurfaceTests(ApiFactory f)
    {
        _factory = f;
        _client = f.CreateClient();
    }

    private async Task<HttpClient> AcmePartnerClient(string? scope = "partner.payments.initiate partner.payments.read")
    {
        var tok = await _factory.IssueClient("acme-treasury-client", "PartnerSecret!ExampleOnly", Audience.Partner, scope);
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
        c.DefaultRequestHeaders.Add("X-Client-Cert-Thumbprint", ThumbprintAcme);
        return c;
    }

    [Fact]
    public async Task Initiate_Payment_Requires_Auth()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/partner/payments", new
        {
            externalReference = "ext-1",
            debtorAccountNumber = "NTSF-0001-ALICE",
            creditorAccountNumber = "NTSF-0003-BOB",
            amountMinorUnits = 500_00m,
            currency = "USD"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Initiate_Payment_Requires_Client_Cert_Thumbprint_Header()
    {
        var tok = await _factory.IssueClient("acme-treasury-client", "PartnerSecret!ExampleOnly", Audience.Partner,
            "partner.payments.initiate partner.payments.read");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
        // No thumbprint header.
        var res = await c.PostAsJsonAsync("/api/v1/partner/payments", new
        {
            externalReference = "ext-2",
            debtorAccountNumber = "NTSF-0001-ALICE",
            creditorAccountNumber = "NTSF-0003-BOB",
            amountMinorUnits = 100_00m,
            currency = "USD"
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Initiate_Payment_Returns_Created_And_Idempotent_On_Replay()
    {
        var c = await AcmePartnerClient();
        var body = new
        {
            externalReference = "idempotent-ref-A",
            debtorAccountNumber = "NTSF-0001-ALICE",
            creditorAccountNumber = "NTSF-0003-BOB",
            amountMinorUnits = 250_00m,
            currency = "USD"
        };
        var first = await c.PostAsJsonAsync("/api/v1/partner/payments", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await c.PostAsJsonAsync("/api/v1/partner/payments", body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // returned existing
    }

    [Fact]
    public async Task Missing_Initiate_Scope_Yields_Forbidden()
    {
        var c = await AcmePartnerClient(scope: "partner.payments.read");
        var body = new
        {
            externalReference = "no-init-scope-1",
            debtorAccountNumber = "NTSF-0001-ALICE",
            creditorAccountNumber = "NTSF-0003-BOB",
            amountMinorUnits = 100_00m,
            currency = "USD"
        };
        var res = await c.PostAsJsonAsync("/api/v1/partner/payments", body);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Service_Token_Cannot_Be_Used_On_Partner_Api()
    {
        var svc = await _factory.IssueService("spiffe://demo/ns/default/sa/billing", Audience.InternalService,
            "internal.billing");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svc.AccessToken);
        c.DefaultRequestHeaders.Add("X-Client-Cert-Thumbprint", ThumbprintAcme);
        var res = await c.PostAsJsonAsync("/api/v1/partner/payments", new
        {
            externalReference = "svc-1",
            debtorAccountNumber = "NTSF-0001-ALICE",
            creditorAccountNumber = "NTSF-0003-BOB",
            amountMinorUnits = 100_00m,
            currency = "USD"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Service_Token_Cannot_Be_Used_On_Customer_Api()
    {
        var svc = await _factory.IssueService("spiffe://demo/ns/default/sa/billing", Audience.InternalService,
            "internal.billing");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svc.AccessToken);
        var res = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Inbound_Webhook_Signature_Missing_Returns_400()
    {
        var res = await _client.PostAsync("/api/v1/partner/webhooks/inbound",
            new StringContent("{\"event\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
