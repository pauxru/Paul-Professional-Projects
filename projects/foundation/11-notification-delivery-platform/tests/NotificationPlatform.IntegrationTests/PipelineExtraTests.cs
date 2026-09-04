namespace NotificationPlatform.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Domain.Providers;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Providers.Common;
using NotificationPlatform.Infrastructure.Seed;
using Xunit;

public sealed class CircuitBreakerTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysFailProvider("BreakerPrimary", NotificationChannel.Email, ProviderResultKind.TransientFailure),
            new AlwaysSucceedProvider("BreakerSecondary", NotificationChannel.Email),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task CircuitOpensOnPrimaryAfterEnoughFailures()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        var dbf = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"CB\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;

        // 6 sends -> primary should transition to Open (default failure threshold 5)
        for (int i = 0; i < 6; i++)
        {
            var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, $"cb-{i}");
            var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
            r.EnsureSuccessStatusCode();
        }
        await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 20, CancellationToken.None);

        await using var db = await dbf.CreateDbContextAsync();
        var primary = await db.ProviderHealth.SingleOrDefaultAsync(h => h.ProviderName == "BreakerPrimary");
        Assert.NotNull(primary);
        Assert.Equal(CircuitState.Open, primary!.State);
    }
}

public sealed class FrequencyCapTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysSucceedProvider("EmailOK", NotificationChannel.Email),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();

        // Alice opts INTO marketing to bypass her existing opt-out check for this test
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        // Nothing to change; Alice is already opted-in per seed.
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task FrequencyCapBlocksMoreThanDailyLimit()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"offers\":[\"a\"]}").RootElement;

        var accepted = 0;
        var suppressed = 0;
        for (int i = 0; i < 6; i++)
        {
            var req = new SendNotificationRequest("marketing.weekly_offer", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Marketing, NotificationCategory.Marketing, null, null, null, $"fc-{i}");
            var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
            var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
            if (o!.Kind == SendOutcomeKind.Accepted) accepted++;
            else if (o.Kind == SendOutcomeKind.Suppressed && o.Message == "frequency_cap") suppressed++;
            if (o.Kind == SendOutcomeKind.Accepted)
                await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 5, CancellationToken.None);
        }
        Assert.Equal(3, accepted);
        Assert.True(suppressed >= 1, $"expected at least 1 frequency-cap suppression, got {suppressed}");
    }
}

public sealed class QuotaTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysSucceedProvider("EmailOK", NotificationChannel.Email),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
        // Reduce contoso hard quota to make the test fast
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == SeedData.ContosoTenantId);
        var t = typeof(NotificationPlatform.Domain.Tenants.Tenant);
        t.GetProperty(nameof(NotificationPlatform.Domain.Tenants.Tenant.MonthlyQuotaHard))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(tenant, new object[] { 3 });
        t.GetProperty(nameof(NotificationPlatform.Domain.Tenants.Tenant.MonthlyQuotaSoft))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(tenant, new object[] { 2 });
        // Ensure EF sees the mutation
        db.Entry(tenant).Property(nameof(NotificationPlatform.Domain.Tenants.Tenant.MonthlyQuotaHard)).IsModified = true;
        db.Entry(tenant).Property(nameof(NotificationPlatform.Domain.Tenants.Tenant.MonthlyQuotaSoft)).IsModified = true;
        await db.SaveChangesAsync();
        // Sanity — reload and verify
        await using var check = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var reloaded = await check.Tenants.AsNoTracking().SingleAsync(x => x.Id == SeedData.ContosoTenantId);
        if (reloaded.MonthlyQuotaHard != 3)
            throw new InvalidOperationException($"quota override did not persist; got {reloaded.MonthlyQuotaHard}");
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task HardQuotaBlocksFourthSend()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"Q\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        int accepted = 0, rejected = 0;
        var messages = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 5; i++)
        {
            var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, $"q-{i}");
            var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
            messages.Add($"[{i}] {(int)r.StatusCode}");
            if (r.StatusCode == HttpStatusCode.Created) accepted++;
            else if (r.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var body = await r.Content.ReadAsStringAsync();
                if (body.Contains("monthly_quota_hard_limit", StringComparison.Ordinal)) rejected++;
            }
        }
        Assert.True(rejected >= 1, $"expected rejection; got accepted={accepted} rejected={rejected}: {string.Join("; ", messages)}");
        Assert.Equal(3, accepted);
    }
}

public sealed class UnsubscribeTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync() { await _factory.EnsureSeededAsync(); }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task IssueAndConsumeValidUnsubscribeToken()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.ManagePreferences);
        var body = new { recipientExternalId = "cust-001", category = "marketing" };
        var issue = await client.PostAsJsonAsync("/api/v1/unsubscribe/issue", body);
        issue.EnsureSuccessStatusCode();
        var token = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync($"/api/v1/unsubscribe/{Uri.EscapeDataString(token!)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("unsubscribed").GetBoolean());
    }

    [Fact]
    public async Task TamperedTokenIsRejected()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.ManagePreferences);
        var body = new { recipientExternalId = "cust-001", category = "marketing" };
        var issue = await client.PostAsJsonAsync("/api/v1/unsubscribe/issue", body);
        issue.EnsureSuccessStatusCode();
        var token = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var tampered = token![..^2] + "AA";

        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync($"/api/v1/unsubscribe/{Uri.EscapeDataString(tampered)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

public sealed class ReceiptWebhookTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysSucceedProvider("EmailOK", NotificationChannel.Email),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task ValidSignedReceiptIsAccepted()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"R\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        var notifId = o!.NotificationId!.Value;

        // Verify DB has the notification (rules out queueing/persistence issues)
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var exists = await db.Notifications.AnyAsync(n => n.Id == notifId);
            Assert.True(exists, $"expected notification {notifId} to be persisted after POST");
        }

        var receiptPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            notificationId = notifId,
            providerName = "ExternalProvider",
            providerMessageId = "ext-msg-1",
            kind = "delivered",
            reason = (string?)null
        });
        var sigService = _factory.Services.GetRequiredService<IWebhookSignatureService>();
        var sig = sigService.Sign(receiptPayload, _factory.Clock.UtcNow);
        var parts = sig.Split('.');
        var anon = _factory.CreateClient();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/receipts")
        {
            Content = new StringContent(receiptPayload, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("X-Signature", parts[0]);
        msg.Headers.Add("X-Timestamp", parts[1]);
        msg.Headers.Add("X-Nonce", Guid.NewGuid().ToString("N"));
        var resp = await anon.SendAsync(msg);
        var respBody = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Accepted, $"Expected Accepted; got {resp.StatusCode} body={respBody}");
    }

    [Fact]
    public async Task InvalidSignatureIsRejected()
    {
        var client = _factory.CreateClient();
        var receiptPayload = System.Text.Json.JsonSerializer.Serialize(new { notificationId = Guid.NewGuid(), providerName = "X", providerMessageId = "y", kind = "delivered" });
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/receipts")
        {
            Content = new StringContent(receiptPayload, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("X-Signature", "not-a-signature");
        msg.Headers.Add("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        msg.Headers.Add("X-Nonce", Guid.NewGuid().ToString("N"));
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ReplayIsRejected()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"RR\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        var notifId = o!.NotificationId!.Value;

        var receiptPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            notificationId = notifId,
            providerName = "ExternalProvider",
            providerMessageId = "ext-msg-2",
            kind = "delivered"
        });
        var sigService = _factory.Services.GetRequiredService<IWebhookSignatureService>();
        var sig = sigService.Sign(receiptPayload, _factory.Clock.UtcNow);
        var parts = sig.Split('.');
        var nonce = Guid.NewGuid().ToString("N");

        HttpRequestMessage Build()
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/receipts")
            {
                Content = new StringContent(receiptPayload, System.Text.Encoding.UTF8, "application/json"),
            };
            m.Headers.Add("X-Signature", parts[0]);
            m.Headers.Add("X-Timestamp", parts[1]);
            m.Headers.Add("X-Nonce", nonce);
            return m;
        }
        var anon = _factory.CreateClient();
        var r1 = await anon.SendAsync(Build());
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var r2 = await anon.SendAsync(Build());
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }
}

public sealed class DeliverySuccessTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysSucceedProvider("EmailOK", NotificationChannel.Email),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task SuccessfulSendReachesDeliveredStatus()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications, Policies.ViewAnalytics);
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"OK\",\"item\":\"pen\",\"total\":\"9.99\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 10, CancellationToken.None);
        var dto = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o!.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.Delivered, dto!.Status);
        Assert.Equal("EmailOK", dto.LastProvider);
    }

    [Fact]
    public async Task TenantFairnessSchedulerDoesNotStarveOtherTenants()
    {
        var contoso = _factory.CreateClient();
        await contoso.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var savanna = _factory.CreateClient();
        await savanna.AsTenantAsync(SeedData.SavannaTenantId, Policies.SendNotifications);

        var payloadC = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"C\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var payloadS = JsonDocument.Parse("{\"user\":{\"firstName\":\"Wanjiku\"},\"shipment\":{\"id\":\"S\"}}").RootElement;

        // Contoso floods the queue
        for (int i = 0; i < 20; i++)
        {
            var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payloadC, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, $"c-{i}");
            await contoso.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        }
        // Savanna adds a small amount
        var savannaIds = new List<Guid>();
        for (int i = 0; i < 2; i++)
        {
            var req = new SendNotificationRequest("shipment.dispatched", NotificationChannel.Sms, "cust-501", payloadS, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, $"s-{i}");
            var r = await savanna.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
            var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
            savannaIds.Add(o!.NotificationId!.Value);
        }
        // Process a small batch -> should not starve Savanna
        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        await pipeline.ProcessDueAsync(5, CancellationToken.None);

        int savannaProcessed = 0;
        foreach (var id in savannaIds)
        {
            var dto = await savanna.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{id}", DemoJson.Options);
            if (dto!.Status != NotificationStatus.Queued && dto.Status != NotificationStatus.Scheduled) savannaProcessed++;
        }
        Assert.True(savannaProcessed >= 1, $"savanna starved: {savannaProcessed} processed");
    }
}
