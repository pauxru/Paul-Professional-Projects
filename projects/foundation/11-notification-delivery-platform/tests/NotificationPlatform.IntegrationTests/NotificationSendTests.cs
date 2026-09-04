namespace NotificationPlatform.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Seed;
using Xunit;

public sealed class NotificationSendTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<HttpClient> ClientAsync(params string[] scopes)
    {
        var c = _factory.CreateClient();
        await c.AsTenantAsync(SeedData.ContosoTenantId, scopes.Length == 0 ? new[] { Policies.SendNotifications, Policies.ManageDlq, Policies.ViewAnalytics, Policies.ManageSuppressions, Policies.ManagePreferences, Policies.ManageTemplates } : scopes);
        return c;
    }

    [Fact]
    public async Task HappyPathSendReturnsAccepted()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"A-100\",\"item\":\"book\",\"total\":\"12.99\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var resp = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var outcome = await resp.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, outcome!.Kind);
        Assert.NotNull(outcome.NotificationId);

        var getResp = await client.GetAsync($"/api/v1/notifications/{outcome.NotificationId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
    }

    [Fact]
    public async Task IdempotencyReplayReturnsOriginal()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"A-101\",\"item\":\"pen\",\"total\":\"1.00\"}}").RootElement;
        var idem = Guid.NewGuid().ToString();
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, idem, null);
        var r1 = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o1 = await r1.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o1!.Kind);

        var r2 = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var o2 = await r2.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.IdempotentReplay, o2!.Kind);
        Assert.Equal(o1.NotificationId, o2.NotificationId);
    }

    [Fact]
    public async Task DedupWindowSuppressesSecondCall()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"A-102\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var dedup = "abc-1";
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, dedup);
        var r1 = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o1 = await r1.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o1!.Kind);
        var r2 = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o2 = await r2.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Deduplicated, o2!.Kind);
    }

    [Fact]
    public async Task SuppressedAddressBlocksSend()
    {
        var client = await ClientAsync();
        // Suppress cust-001's email address
        var supp = new
        {
            channel = NotificationChannel.Email,
            address = "alice@contoso.example",
            reason = NotificationPlatform.Domain.Common.SuppressionReason.Unsubscribe,
            notes = "user unsubscribed"
        };
        var addResp = await client.PostAsJsonAsync("/api/v1/suppressions", supp, DemoJson.Options);
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);

        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"S-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Suppressed, o!.Kind);
    }

    [Fact]
    public async Task OptedOutRecipientIsSuppressedForMarketing()
    {
        var client = await ClientAsync();
        // Bob is pre-seeded as opted out of Marketing Email at Contoso
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Bob\"},\"offers\":[\"deal1\"]}").RootElement;
        var req = new SendNotificationRequest("marketing.weekly_offer", NotificationChannel.Email, "cust-002", payload, NotificationPriority.Marketing, NotificationCategory.Marketing, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Suppressed, o!.Kind);
    }

    [Fact]
    public async Task NotFoundRecipientReturns422()
    {
        var client = await ClientAsync();
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "not-a-real-user", null, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task QuietHoursDefersMarketingButNotTransactional()
    {
        // Alice is en-US/New_York, quiet hours 22:00-07:00 local.
        // Set fake clock to 03:00 UTC 2026-09-03 = 23:00 NYC previous day = inside quiet.
        _factory.Clock.Set(new DateTimeOffset(2026, 09, 03, 03, 0, 0, TimeSpan.Zero));
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"offers\":[\"deal\"]}").RootElement;
        var marketing = new SendNotificationRequest("marketing.weekly_offer", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Marketing, NotificationCategory.Marketing, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", marketing, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o!.Kind);
        var dto = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.Scheduled, dto!.Status);

        var payload2 = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"T-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var txn = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload2, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r2 = await client.PostAsJsonAsync("/api/v1/notifications", txn, DemoJson.Options);
        var o2 = await r2.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        var dto2 = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o2!.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.Queued, dto2!.Status);
    }

    [Fact]
    public async Task ScheduledSendPersistsFutureDate()
    {
        _factory.Clock.Set(new DateTimeOffset(2026, 09, 03, 12, 0, 0, TimeSpan.Zero));
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"S-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var sendAt = new DateTimeOffset(2026, 09, 03, 15, 0, 0, TimeSpan.Zero);
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, sendAt, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o!.Kind);
        var dto = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.Scheduled, dto!.Status);
    }

    [Fact]
    public async Task BulkSendReturnsPerItemResults()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"B-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var items = new[]
        {
            new BulkSendItem("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null),
            new BulkSendItem("order.confirmation", NotificationChannel.Email, "does-not-exist", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null),
        };
        var body = new BulkSendRequest(null, items);
        var resp = await client.PostAsJsonAsync("/api/v1/notifications/bulk", body, DemoJson.Options);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var outcome = await resp.Content.ReadFromJsonAsync<BulkSendOutcome>(DemoJson.Options);
        Assert.NotNull(outcome);
        Assert.Equal(2, outcome!.Results.Count);
        Assert.Equal(SendOutcomeKind.Accepted, outcome.Results[0].Kind);
        Assert.Equal(SendOutcomeKind.Rejected, outcome.Results[1].Kind);
    }

    [Fact]
    public async Task ListNotificationsReturnsPagedResponse()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"L\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        for (int i = 0; i < 3; i++)
        {
            var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, $"L{i}");
            var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
            r.EnsureSuccessStatusCode();
        }
        var resp = await client.GetAsync("/api/v1/notifications?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(DemoJson.Options);
        Assert.True(json.GetProperty("totalCount").GetInt32() >= 3);
    }
}
