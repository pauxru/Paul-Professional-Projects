using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using IntegrationHub.Simulators.Crm;
using IntegrationHub.Simulators.Erp;
using IntegrationHub.Simulators.Payments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationHub.IntegrationTests;

public sealed class ConnectorSimulatorTests
{
    [Fact]
    public async Task PageNumberPagination_AgainstCrmSimulator_ReadsAllContacts()
    {
        using var factory = new WebApplicationFactory<CrmApiMarker>();
        var connector = Rest(factory.CreateClient(), PaginationStyle.PageNumber, "/api/contacts", "$.items", ConnectorAuthKind.ApiKey);
        var result = await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("test", PageSize: 2));
        Assert.Equal(4, result.RecordsRead);
    }

    [Fact]
    public async Task OffsetPagination_AgainstCrmSimulator_ReadsAllAccounts()
    {
        using var factory = new WebApplicationFactory<CrmApiMarker>();
        var connector = Rest(factory.CreateClient(), PaginationStyle.Offset, "/api/accounts", "$.items", ConnectorAuthKind.ApiKey);
        var result = await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("test", PageSize: 2));
        Assert.Equal(4, result.RecordsRead);
    }

    [Fact]
    public async Task CursorPagination_AgainstCrmSimulator_ReadsAllOpportunities()
    {
        using var factory = new WebApplicationFactory<CrmApiMarker>();
        var connector = Rest(factory.CreateClient(), PaginationStyle.Cursor, "/api/opportunities", "$.items", ConnectorAuthKind.ApiKey);
        var result = await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("test", PageSize: 2));
        Assert.Equal(4, result.RecordsRead);
    }

    [Fact]
    public async Task LinkHeaderPagination_AgainstErpSimulator_ReadsAllProducts()
    {
        using var factory = new WebApplicationFactory<ErpApiMarker>();
        var connector = Rest(factory.CreateClient(), PaginationStyle.LinkHeader, "/api/products", "$.items", ConnectorAuthKind.Basic);
        var result = await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("test", PageSize: 2));
        Assert.Equal(4, result.RecordsRead);
    }

    [Fact]
    public async Task OAuthToken_TwoCalls_UsesCachedToken()
    {
        using var factory = new WebApplicationFactory<PaymentsApiMarker>();
        var connector = PaymentConnector(factory.CreateClient());
        await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("one", PageSize: 2));
        await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("two", PageSize: 2));
        Assert.Equal(1, factory.Services.GetRequiredService<PaymentState>().TokenSequence);
    }

    [Fact]
    public async Task OAuthToken_401_RefreshesTokenAndRetries()
    {
        using var factory = new WebApplicationFactory<PaymentsApiMarker>();
        var connector = PaymentConnector(factory.CreateClient());
        await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("one", PageSize: 2));
        factory.Services.GetRequiredService<PaymentFaultState>().RejectTokenOnce = 1;
        var result = await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("two", PageSize: 2));
        Assert.Equal(4, result.RecordsRead);
        Assert.Equal(2, factory.Services.GetRequiredService<PaymentState>().TokenSequence);
    }

    [Fact]
    public async Task RetryAfter_FromCrm429_IsHonouredBeforeRetry()
    {
        using var factory = new WebApplicationFactory<CrmApiMarker>();
        var fault = factory.Services.GetRequiredService<FaultState>();
        fault.ThrottleNext = 1;
        fault.RetryAfterSeconds = 3;
        var delays = new List<TimeSpan>();
        var retry = new RetryExecutor((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var connector = Rest(
            factory.CreateClient(), PaginationStyle.PageNumber, "/api/contacts", "$.items",
            ConnectorAuthKind.ApiKey, retry);
        var result = await connector.ExecuteAsync("list", null, new ConnectorExecutionContext("retry", PageSize: 10));
        Assert.Equal(4, result.RecordsRead);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(delays));
    }

    [Fact]
    public async Task Simulator_EtagMismatch_ReturnsPreconditionFailed()
    {
        using var factory = new WebApplicationFactory<CrmApiMarker>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-only-simulator-key");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/contacts/crm-001")
        {
            Content = new StringContent(
                """{"id":"crm-001","displayName":"Changed","email":"changed@example.test","currency":"KES"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"999\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Simulator_MissingAuthentication_Returns401()
    {
        using var factory = new WebApplicationFactory<CrmApiMarker>();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/contacts?page=1&pageSize=2")).StatusCode);
    }

    private static RestConnector Rest(
        HttpClient client,
        PaginationStyle pagination,
        string path,
        string responsePath,
        ConnectorAuthKind authKind,
        RetryExecutor? retry = null)
    {
        var descriptor = Descriptor("test", authKind, pagination);
        var auth = authKind switch
        {
            ConnectorAuthKind.ApiKey => new RestAuthenticationOptions(authKind, "X-Api-Key", "@secret:api"),
            ConnectorAuthKind.Basic => new RestAuthenticationOptions(authKind, Username: "@secret:user", Password: "@secret:password"),
            _ => new RestAuthenticationOptions(authKind)
        };
        return new RestConnector(
            client,
            new RestConnectorOptions(
                descriptor,
                client.BaseAddress!,
                auth,
                new Dictionary<string, RestOperationOptions>
                {
                    ["list"] = new("list", "GET", path, ResponsePath: responsePath, PaginationStyle: pagination)
                }),
            new SecretReferenceResolver(new DictionarySecretStore(new Dictionary<string, string>
            {
                ["api"] = "dev-only-simulator-key",
                ["user"] = "demo",
                ["password"] = "dev-only-simulator-password"
            })),
            new ConnectorUrlGuard(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { client.BaseAddress!.Host }, true, true),
            new TestClock(),
            retry);
    }

    private static RestConnector PaymentConnector(HttpClient client)
    {
        var descriptor = Descriptor("payments", ConnectorAuthKind.OAuth2ClientCredentials, PaginationStyle.Cursor);
        return new RestConnector(
            client,
            new RestConnectorOptions(
                descriptor,
                client.BaseAddress!,
                new RestAuthenticationOptions(
                    ConnectorAuthKind.OAuth2ClientCredentials,
                    TokenEndpoint: "/oauth/token",
                    ClientId: "@secret:client",
                    ClientSecret: "@secret:secret"),
                new Dictionary<string, RestOperationOptions>
                {
                    ["list"] = new("list", "GET", "/api/settlements", ResponsePath: "$.items", PaginationStyle: PaginationStyle.Cursor)
                }),
            new SecretReferenceResolver(new DictionarySecretStore(new Dictionary<string, string>
            {
                ["client"] = "demo-client",
                ["secret"] = "dev-only-simulator-secret"
            })),
            new ConnectorUrlGuard(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { client.BaseAddress!.Host }, true, true),
            new TestClock());
    }

    private static ConnectorDescriptor Descriptor(string id, ConnectorAuthKind auth, PaginationStyle pagination) =>
        new(id, id, "1.0.0", auth,
            [new ConnectorOperationDescriptor("list", "GET", "/", null, null, true)],
            new RateLimitDescriptor(100, TimeSpan.FromMinutes(1), 4),
            pagination,
            "test");
}

internal sealed class DictionarySecretStore(IReadOnlyDictionary<string, string> values) : ISecretStore
{
    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(values[name]);
    public Task<int> RotateAsync(string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyCollection<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<SecretMetadata>>([]);
}

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
