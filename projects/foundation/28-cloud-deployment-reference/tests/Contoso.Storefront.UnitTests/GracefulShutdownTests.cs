using Contoso.Storefront.Api.Hosting;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.UnitTests;

public sealed class GracefulShutdownTests
{
    [Fact]
    public void RequestDrainTracker_AfterDrainBegins_RejectsNewRequests()
    {
        var tracker = new RequestDrainTracker();

        tracker.BeginDraining();

        Assert.True(tracker.IsDraining);
        Assert.Null(tracker.TryEnter());
    }

    [Fact]
    public async Task RequestDrainTracker_WithInflightRequest_WaitsForCompletion()
    {
        var tracker = new RequestDrainTracker();
        var lease = tracker.TryEnter();
        tracker.BeginDraining();

        var waiting = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        lease!.Dispose();

        Assert.True(await waiting);
        Assert.Equal(0, tracker.InFlight);
    }

    [Fact]
    public async Task GracefulShutdownService_StopAsync_DrainsInflightWorkBeforeCompleting()
    {
        var tracker = new RequestDrainTracker();
        var lease = tracker.TryEnter();
        var service = new GracefulShutdownService(
            tracker,
            new ImmediateDelay(),
            new TestApplicationLifetime(),
            Options.Create(new OperationalOptions
            {
                PreStopDelaySeconds = 0,
                DrainTimeoutSeconds = 2
            }),
            NullLogger<GracefulShutdownService>.Instance);

        var stopping = service.StopAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.True(tracker.IsDraining);
        Assert.False(stopping.IsCompleted);

        lease!.Dispose();
        await stopping;
        Assert.Equal(0, tracker.InFlight);
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }
}
