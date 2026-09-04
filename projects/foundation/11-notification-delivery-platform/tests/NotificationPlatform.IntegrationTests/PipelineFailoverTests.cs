namespace NotificationPlatform.IntegrationTests;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Seed;
using Xunit;

public sealed class PipelineFailoverTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    private AlwaysFailProvider _primary = null!;
    private AlwaysSucceedProvider _secondary = null!;

    public async Task InitializeAsync()
    {
        _primary = new AlwaysFailProvider("PrimaryEmailFail", NotificationChannel.Email, ProviderResultKind.TransientFailure);
        _secondary = new AlwaysSucceedProvider("SecondaryEmailOK", NotificationChannel.Email);
        _factory.UseProviders(
            _primary,
            _secondary,
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<HttpClient> ClientAsync()
    {
        var c = _factory.CreateClient();
        await c.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications, Policies.ManageDlq);
        return c;
    }

    [Fact]
    public async Task FailoverPrimaryToSecondaryOnTransient()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"F-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o!.Kind);

        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        var processed = await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 10, CancellationToken.None);
        Assert.True(processed > 0);

        var dto = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o.NotificationId}", DemoJson.Options);
        Assert.NotEqual(NotificationStatus.Failed, dto!.Status);
        Assert.True(_primary.Calls >= 1, "primary should have been called");
        Assert.True(_secondary.Calls >= 1, "secondary should have been called after failover");
        Assert.Equal(_secondary.Name, dto.LastProvider);
    }
}

public sealed class PermanentFailureTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysFailProvider("PermFail1", NotificationChannel.Email, ProviderResultKind.PermanentFailure),
            new AlwaysFailProvider("PermFail2", NotificationChannel.Email, ProviderResultKind.PermanentFailure),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task PermanentFailureMarksNotificationFailedAfterAllProviders()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"D-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o!.Kind);
        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 10, CancellationToken.None);
        var dto = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.Failed, dto!.Status);
    }
}

public sealed class DeadLetterAndReplayTests : IAsyncLifetime
{
    private readonly NotificationAppFactory _factory = new();
    public async Task InitializeAsync()
    {
        _factory.UseProviders(
            new AlwaysFailProvider("TrFail1", NotificationChannel.Email, ProviderResultKind.TransientFailure),
            new AlwaysFailProvider("TrFail2", NotificationChannel.Email, ProviderResultKind.TransientFailure),
            new AlwaysSucceedProvider("SmsOK", NotificationChannel.Sms),
            new AlwaysSucceedProvider("PushOK", NotificationChannel.Push),
            new AlwaysSucceedProvider("WebhookOK", NotificationChannel.Webhook));
        await _factory.EnsureSeededAsync();
    }
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<HttpClient> ClientAsync()
    {
        var c = _factory.CreateClient();
        await c.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications, Policies.ManageDlq);
        return c;
    }

    [Fact]
    public async Task TransientFailureRetriesUpToMaxAndDeadLetters()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"DLQ-1\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        Assert.Equal(SendOutcomeKind.Accepted, o!.Kind);

        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        for (int i = 0; i < 12; i++)
        {
            _factory.Clock.Advance(TimeSpan.FromSeconds(60));
            await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 10, CancellationToken.None);
        }
        var dto = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.DeadLettered, dto!.Status);
        var dlqResp = await client.GetAsync("/api/v1/admin/dlq");
        dlqResp.EnsureSuccessStatusCode();
        var dlqJson = await dlqResp.Content.ReadFromJsonAsync<JsonElement>(DemoJson.Options);
        Assert.True(dlqJson.GetProperty("totalCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task DlqReplayRequeuesItem()
    {
        var client = await ClientAsync();
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"DLQ-R\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;
        var req = new SendNotificationRequest("order.confirmation", NotificationChannel.Email, "cust-001", payload, NotificationPriority.Transactional, NotificationCategory.Transactional, null, null, null, null);
        var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
        var o = await r.Content.ReadFromJsonAsync<SendOutcome>(DemoJson.Options);
        var pipeline = _factory.Services.GetRequiredService<IDeliveryPipeline>();
        for (int i = 0; i < 12; i++)
        {
            _factory.Clock.Advance(TimeSpan.FromSeconds(60));
            await pipeline.ProcessTenantAsync(SeedData.ContosoTenantId, 10, CancellationToken.None);
        }
        var replay = await client.PostAsJsonAsync("/api/v1/admin/dlq/replay", new[] { o!.NotificationId!.Value }, DemoJson.Options);
        replay.EnsureSuccessStatusCode();
        var replayResult = await replay.Content.ReadFromJsonAsync<JsonElement>(DemoJson.Options);
        Assert.Equal(1, replayResult.GetProperty("replayed").GetInt32());
        var after = await client.GetFromJsonAsync<NotificationDto>($"/api/v1/notifications/{o.NotificationId}", DemoJson.Options);
        Assert.Equal(NotificationStatus.Queued, after!.Status);
    }
}
