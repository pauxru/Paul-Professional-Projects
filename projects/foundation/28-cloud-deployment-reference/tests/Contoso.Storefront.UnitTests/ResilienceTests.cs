using System.Diagnostics;
using System.Net;
using Contoso.Storefront.Application.Configuration;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Infrastructure.Http;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.UnitTests;

public sealed class ResilienceTests
{
    [Fact]
    public async Task ResilienceHandler_TransientFailures_RetriesUntilSuccess()
    {
        var inner = new SequenceHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var delay = new RecordingDelay();
        using var invoker = CreateInvoker(inner, delay, retryCount: 2);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(2, delay.Delays.Count);
    }

    [Fact]
    public async Task ResilienceHandler_NonTransientClientError_DoesNotRetry()
    {
        var inner = new SequenceHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var invoker = CreateInvoker(inner, new RecordingDelay(), retryCount: 3);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task ResilienceHandler_AfterFailureThreshold_OpensCircuit()
    {
        var inner = new SequenceHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var invoker = CreateInvoker(
            inner,
            new RecordingDelay(),
            retryCount: 0,
            failureThreshold: 1);

        var first = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        await Assert.ThrowsAsync<CircuitOpenException>(
            () => invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
                CancellationToken.None));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task ResilienceHandler_AfterBreakDuration_HalfOpenProbeCanCloseCircuit()
    {
        var clock = new FakeClock();
        var inner = new SequenceHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var invoker = CreateInvoker(
            inner,
            new RecordingDelay(),
            retryCount: 0,
            failureThreshold: 1,
            breakDurationMs: 1_000,
            clock);

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));

        var recovered = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task ResilienceHandler_Timeout_RetriesThenThrowsTimeout()
    {
        var inner = new SequenceHandler(
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var invoker = CreateInvoker(
            inner,
            new RecordingDelay(),
            retryCount: 1,
            timeoutMs: 20);

        await Assert.ThrowsAsync<TimeoutException>(
            () => invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
                CancellationToken.None));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CorrelationHandler_OutboundRequest_PropagatesCorrelationAndW3CTraceParent()
    {
        var context = new TestCorrelationContext { CorrelationId = "correlation-123" };
        var capture = new CaptureHandler();
        using var handler = new CorrelationPropagationHandler(context)
        {
            InnerHandler = capture
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var activity = new Activity("test-outbound")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"),
            CancellationToken.None);

        Assert.Equal("correlation-123", capture.CorrelationId);
        Assert.NotNull(capture.TraceParent);
        Assert.StartsWith("00-", capture.TraceParent, StringComparison.Ordinal);
    }

    private static HttpMessageInvoker CreateInvoker(
        HttpMessageHandler inner,
        RecordingDelay delay,
        int retryCount,
        int failureThreshold = 3,
        int breakDurationMs = 5_000,
        FakeClock? clock = null,
        int timeoutMs = 2_000)
    {
        var handler = new ResilientHttpMessageHandler(
            Options.Create(new ResilienceOptions
            {
                RetryCount = retryCount,
                RetryBaseDelayMs = 1,
                CircuitFailureThreshold = failureThreshold,
                CircuitBreakDurationMs = breakDurationMs,
                TimeoutMs = timeoutMs
            }),
            clock ?? new FakeClock(),
            delay)
        {
            InnerHandler = inner
        };
        return new HttpMessageInvoker(handler);
    }

    private sealed class SequenceHandler(
        params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
    {
        private int _index;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var index = Math.Min(_index++, responses.Length - 1);
            return responses[index](cancellationToken);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? CorrelationId { get; private set; }
        public string? TraceParent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CorrelationId = request.Headers.GetValues("X-Correlation-Id").Single();
            TraceParent = request.Headers.GetValues("traceparent").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class RecordingDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan amount) => UtcNow += amount;
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string? CorrelationId { get; set; }
    }
}
