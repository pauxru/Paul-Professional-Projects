using System.Net.Http.Json;
using System.Text.Json;
using AuditPlatform.Application.Events;
using AuditPlatform.Domain.Events;

namespace AuditPlatform.IntegrationTests;

internal static class TestData
{
    public static object SampleIngestBody(string eventType = "user.login", string clientEventId = "client-1", DateTimeOffset? eventTime = null) => new
    {
        EventType = eventType,
        SchemaVersion = 1,
        EventTime = eventTime ?? DateTimeOffset.UtcNow,
        Actor = new { Type = 0, Id = "u-1", DisplayName = "Test User", Roles = new[] { "banker" } },
        ActionVerb = "login",
        Category = 1,
        Resource = new { Type = "session", Id = "s-1", Name = "sign-in", ParentPath = "/auth" },
        Outcome = 0,
        Severity = 0,
        Source = new { Ip = "10.0.0.1", UserAgent = "test", Service = "test-svc", Region = "eu" },
        CorrelationId = "corr-1",
        CausationId = (string?)null,
        TraceId = (string?)null,
        ClientEventId = clientEventId,
        Data = JsonDocument.Parse("{\"method\":\"password\"}").RootElement,
        Before = (JsonElement?)null,
        After = (JsonElement?)null
    };

    public static async Task RegisterUserLoginSchema(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/v1/schemas", new
        {
            EventType = "user.login",
            SchemaJson = "{ \"fields\": [ {\"name\":\"method\",\"kind\":\"string\",\"required\":true} ] }",
            Description = "User signed in"
        });
    }

    public static async Task RegisterDeleteSchema(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/v1/schemas", new
        {
            EventType = "account.delete",
            SchemaJson = "{ \"fields\": [ {\"name\":\"accountId\",\"kind\":\"string\",\"required\":true} ] }",
            Description = "Account deleted"
        });
    }
}
