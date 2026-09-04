using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.IntegrationTests.Fixtures;

namespace ZeroTrust.IntegrationTests.Endpoints;

public class RateLimitTests : IClassFixture<LowLimitFactory>
{
    private readonly LowLimitFactory _factory;
    private const string ThumbprintAcme = "AA11BB22CC33DD44EE55FF66AA11BB22CC33DD44";

    public RateLimitTests(LowLimitFactory f) { _factory = f; }

    [Fact]
    public async Task Partner_Rate_Limit_Triggers_429_With_Retry_After()
    {
        // Issue a partner token via DI
        using var s = _factory.Services.CreateScope();
        var issuer = s.ServiceProvider.GetRequiredService<ITokenIssuer>();
        var tok = await issuer.IssueAsync(new TokenRequest("client_credentials", null, null,
            "acme-treasury-client", "PartnerSecret!ExampleOnly",
            "partner.payments.read", Audience.Partner, null, null, null),
            CancellationToken.None);

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
        c.DefaultRequestHeaders.Add("X-Client-Cert-Thumbprint", ThumbprintAcme);

        // With permits=6, the partner should exhaust its bucket well within 60 iterations.
        HttpStatusCode? last = null;
        for (var i = 0; i < 60; i++)
        {
            var res = await c.GetAsync($"/api/v1/partner/payments/{Guid.NewGuid()}");
            last = res.StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
            {
                Assert.True(res.Headers.Contains("Retry-After"));
                return;
            }
        }
        Assert.Fail($"expected TooManyRequests within 60 iterations, last was {last}");
    }
}
