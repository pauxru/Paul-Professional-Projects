using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Models;

namespace AgentPlatform.Infrastructure.Models;

/// <summary>
/// Wraps another model and records every completion it returns, in order. Recording a run this way
/// captures the exact model responses so the run can later be replayed deterministically.
/// </summary>
public sealed class RecordingChatModel : IChatModel
{
    private readonly IChatModel _inner;
    private readonly List<ChatCompletion> _recordings = new();

    public RecordingChatModel(IChatModel inner) => _inner = inner;

    public string ModelId => _inner.ModelId;

    public IReadOnlyList<ChatCompletion> Recordings => _recordings;

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var completion = await _inner.CompleteAsync(request, cancellationToken);
        _recordings.Add(completion);
        return completion;
    }
}

/// <summary>
/// Replays recorded model completions in order. This is how agent behaviour is regression-tested:
/// a run replayed against its recorded responses must reproduce exactly the same trace. The replay
/// model makes no decisions and touches no network.
/// </summary>
public sealed class ReplayModel : IChatModel
{
    private readonly IReadOnlyList<ChatCompletion> _recordings;
    private int _index;

    public ReplayModel(IReadOnlyList<ChatCompletion> recordings) => _recordings = recordings;

    public string ModelId => "replay-model";

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        if (_index >= _recordings.Count)
            throw new InvalidOperationException("ReplayModel exhausted: more model calls were made than were recorded.");
        return Task.FromResult(_recordings[_index++]);
    }
}
