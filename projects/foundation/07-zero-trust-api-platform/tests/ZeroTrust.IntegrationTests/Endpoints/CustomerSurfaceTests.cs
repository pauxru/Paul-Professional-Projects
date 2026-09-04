using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.IntegrationTests.Fixtures;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class CustomerSurfaceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public CustomerSurfaceTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<HttpClient> AliceCustomerClient(string scope = "customer.read customer.write")
    {
        var token = await _factory.IssueUserAccess("alice", "CustomerPassw0rd!", Audience.Customer, scope);
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task No_Token_Returns_401()
    {
        var res = await _client.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Alice_Sees_Only_Her_Own_Accounts()
    {
        var c = await AliceCustomerClient();
        var res = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var arr = await res.Content.ReadFromJsonAsync<JsonElement>();
        var accts = arr.EnumerateArray().Select(a => a.GetProperty("accountNumber").GetString()).ToArray();
        Assert.All(accts, an => Assert.Contains("ALICE", an));
    }

    [Fact]
    public async Task Alice_Cannot_Read_Bobs_Account_By_Guessing_Id()
    {
        // Ensure fixture seeded, then look up Bob's account.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var bobsAccount = await db.Accounts.FirstAsync(a => a.OwnerSubject == "bob");

        var c = await AliceCustomerClient();
        var res = await c.GetAsync($"/api/v1/customer/accounts/{bobsAccount.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Alice_Can_Read_Her_Own_Account_By_Id()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZeroTrustDbContext>();
        var alicesAccount = await db.Accounts.FirstAsync(a => a.OwnerSubject == "alice");

        var c = await AliceCustomerClient();
        var res = await c.GetAsync($"/api/v1/customer/accounts/{alicesAccount.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_Audience_Is_Rejected()
    {
        // Issue token with admin audience but customer endpoint expects customer audience.
        var token = await _factory.IssueUserAccess("admin", "AdminPassw0rd!", Audience.Admin, "admin.audit", "mfa", "urn:ntsf:acr:step-up");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Alg_None_Tampered_Token_Rejected()
    {
        // Build a header/payload manually with alg=none.
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = B64($"{{\"sub\":\"alice\",\"aud\":\"{Audience.Customer}\",\"iss\":\"https://zero-trust-demo.localhost\",\"exp\":{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}");
        var noneToken = $"{header}.{payload}.";
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", noneToken);
        var res = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Missing_Scope_Yields_Forbidden()
    {
        // Issue a token with no customer.read scope.
        var token = await _factory.IssueUserAccess("alice", "CustomerPassw0rd!", Audience.Customer, "customer.write");
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Correlation_Id_Echoed()
    {
        var c = await AliceCustomerClient();
        c.DefaultRequestHeaders.Add("X-Correlation-Id", "my-cid-12345");
        var res = await c.GetAsync("/api/v1/customer/accounts");
        Assert.Equal("my-cid-12345", res.Headers.GetValues("X-Correlation-Id").First());
    }

    [Fact]
    public async Task Security_Headers_Present()
    {
        var res = await _client.GetAsync("/");
        Assert.True(res.Headers.Contains("Strict-Transport-Security") ||
                    res.Content.Headers.Contains("Strict-Transport-Security"));
        Assert.Contains("nosniff", res.Headers.GetValues("X-Content-Type-Options").First());
    }
}
