using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Api.Auth;
using RagAssistant.Api.Endpoints;
using RagAssistant.Domain.Documents;

namespace RagAssistant.IntegrationTests.Fixtures;

public static class TestAuth
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HttpClient> AuthenticatedClientAsync(
        this ApiFactory factory,
        string userId,
        IEnumerable<string> roles,
        IEnumerable<string> departments,
        Classification classification)
    {
        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<IDevTokenIssuer>();
        var token = issuer.Issue(userId, roles.ToArray(), departments.ToArray(), classification, TimeSpan.FromHours(1));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await Task.CompletedTask;
        return client;
    }
}
