namespace NotificationPlatform.IntegrationTests;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Templates;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Seed;
using NotificationPlatform.Domain.Common;
using Xunit;

/// <summary>
/// Not part of the assert-driven test suite — a driver used to produce numbers
/// for docs/throughput-test.md.  Marked Skip so `dotnet test` does not run it.
/// Remove the Skip to run locally with `dotnet test --filter "FullyQualifiedName~ThroughputDriver"`.
/// </summary>
public sealed class ThroughputDriver : IAsyncLifetime
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

    [Fact(Skip = "throughput driver — remove Skip to run locally")]
    public async Task Enqueue_1000_Transactional_Emails()
    {
        var client = _factory.CreateClient();
        await client.AsTenantAsync(SeedData.ContosoTenantId, Policies.SendNotifications);
        var payload = JsonDocument.Parse("{\"user\":{\"firstName\":\"Alice\"},\"order\":{\"id\":\"P\",\"item\":\"x\",\"total\":\"1\"}}").RootElement;

        int N = 1000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            var req = new SendNotificationRequest(
                "order.confirmation", NotificationChannel.Email, "cust-001",
                payload, NotificationPriority.Transactional, NotificationCategory.Transactional,
                null, null, null, null);
            var r = await client.PostAsJsonAsync("/api/v1/notifications", req, DemoJson.Options);
            r.EnsureSuccessStatusCode();
        }
        sw.Stop();
        var perSec = N / sw.Elapsed.TotalSeconds;

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var queued = await db.Notifications.CountAsync();

        System.Console.WriteLine($"[ThroughputDriver] enqueued {N} in {sw.Elapsed.TotalSeconds:F2}s ≈ {perSec:F0} req/s  (rows in DB={queued})");
    }
}
