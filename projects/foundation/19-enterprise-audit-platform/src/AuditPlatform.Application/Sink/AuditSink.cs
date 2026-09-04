using System.Threading.Channels;
using AuditPlatform.Application.Events;

namespace AuditPlatform.Application.Sink;

/// <summary>
/// In-process sink other services use to emit audit events. Buffers into a channel and drains
/// on a background flusher. On failure, events fall into a memory retry queue with exponential
/// backoff (capped at 5 attempts) — after which the caller is responsible for surfacing loss.
/// </summary>
public sealed class AuditSink : IAsyncDisposable
{
    private readonly Channel<IngestEventRequest> _channel;
    private readonly Func<IngestEventRequest, CancellationToken, Task<IngestResult>> _dispatch;
    private readonly Task _pump;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IngestEventRequest> _lost = new();
    private readonly object _lostLock = new();

    public AuditSink(Func<IngestEventRequest, CancellationToken, Task<IngestResult>> dispatch)
    {
        _dispatch = dispatch;
        _channel = Channel.CreateBounded<IngestEventRequest>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pump = Task.Run(PumpAsync);
    }

    public async Task EmitAsync(IngestEventRequest request, CancellationToken ct)
        => await _channel.Writer.WriteAsync(request, ct);

    public IReadOnlyList<IngestEventRequest> Lost
    {
        get { lock (_lostLock) return _lost.ToList(); }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                var attempts = 0;
                while (true)
                {
                    try
                    {
                        var result = await _dispatch(evt, _cts.Token);
                        if (result.Accepted) break;
                        if (result.Reason?.StartsWith("SchemaValidationFailed", StringComparison.Ordinal) == true ||
                            result.Reason?.StartsWith("UnknownSchema", StringComparison.Ordinal) == true)
                        {
                            lock (_lostLock) _lost.Add(evt);
                            break;
                        }
                    }
                    catch when (attempts < 5)
                    {
                        attempts++;
                        await Task.Delay(TimeSpan.FromMilliseconds(20 * Math.Pow(2, attempts)), _cts.Token);
                        continue;
                    }
                    catch
                    {
                        lock (_lostLock) _lost.Add(evt);
                        break;
                    }
                    if (++attempts >= 5)
                    {
                        lock (_lostLock) _lost.Add(evt);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Cancel();
    }
}
