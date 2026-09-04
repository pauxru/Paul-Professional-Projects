using System.Collections.Concurrent;
using Collab.Application.Contracts;
using Collab.Application.Serialization;
using Collab.Domain.Crdt;
using Collab.Domain.Documents;
using Collab.Domain.Structured;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Collab.IntegrationTests.Harness;

/// <summary>
/// A real SignalR client that mirrors how a browser client behaves: it keeps a local CRDT replica,
/// applies its own edits optimistically, submits them to the authoritative server, and merges the
/// operations it receives from other clients. Convergence assertions compare these local replicas.
/// </summary>
public sealed class CollabClient : IAsyncDisposable
{
    private readonly LamportClock _clock = new();
    private RgaDocument _text = new();
    private StructuredDocument _structured = new();
    private DocumentType _type;
    private Guid _documentId;

    public string ReplicaId { get; }
    public HubConnection Connection { get; }

    public ConcurrentQueue<PresenceSnapshot> PresenceEvents { get; } = new();
    public ConcurrentQueue<NotificationDto> NotificationEvents { get; } = new();
    public ConcurrentQueue<OperationRejected> Rejections { get; } = new();
    public ConcurrentQueue<CommentDto> CommentEvents { get; } = new();

    public int RemoteBatchCount { get; private set; }
    public bool Closed { get; private set; }

    private CollabClient(HubConnection connection, string replicaId)
    {
        Connection = connection;
        ReplicaId = replicaId;

        connection.On<OperationBroadcast>("OperationApplied", ApplyRemote);
        connection.On<PresenceSnapshot>("PresenceChanged", p => PresenceEvents.Enqueue(p));
        connection.On<NotificationDto>("NotificationReceived", n => NotificationEvents.Enqueue(n));
        connection.On<OperationRejected>("Rejected", r => Rejections.Enqueue(r));
        connection.On<CommentDto>("CommentAdded", c => CommentEvents.Enqueue(c));
        connection.Closed += _ => { Closed = true; return Task.CompletedTask; };
    }

    /// <summary>
    /// Build a client over the TestServer. Long-polling is used deliberately: it is the most robust
    /// SignalR transport across <see cref="WebApplicationFactory{T}"/>'s in-memory server, and the
    /// bearer token authenticates the hub exactly as a real deployment would.
    /// </summary>
    public static CollabClient Create(CollabAppFactory factory, string token, string replicaId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/collaboration", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        return new CollabClient(connection, replicaId);
    }

    public Task StartAsync(CancellationToken ct) => Connection.StartAsync(ct);

    public async Task<DocumentStateDto> JoinAsync(Guid documentId, DocumentType type, CancellationToken ct)
    {
        _documentId = documentId;
        _type = type;
        var state = await Connection.InvokeAsync<DocumentStateDto>("JoinDocument", documentId, ct);
        SeedFrom(state);
        return state;
    }

    /// <summary>
    /// Point the client at a document WITHOUT joining its broadcast group — used to simulate a client
    /// that is offline for a window of operations and later catches up via an explicit resync.
    /// </summary>
    public void Track(Guid documentId, DocumentType type)
    {
        _documentId = documentId;
        _type = type;
    }

    private void SeedFrom(DocumentStateDto state)
    {
        if (_type == DocumentType.Text)
        {
            _text = RgaDocument.FromState(CollabJson.Deserialize<RgaState>(state.State));
            _clock.Observe(_text.MaxLamport);
        }
        else
        {
            _structured = StructuredDocument.FromState(CollabJson.Deserialize<StructuredState>(state.State));
            _clock.Observe(_structured.MaxLamport);
        }
    }

    /// <summary>Insert text locally (optimistically) then submit to the server; returns the ack.</summary>
    public async Task<OperationAck> TypeAsync(int index, string text, CancellationToken ct)
    {
        var ops = _text.BuildInsert(index, text, _clock, ReplicaId);
        foreach (var op in ops) _text.Apply(op);
        var envelope = new OperationEnvelope(
            _documentId, ReplicaId, ops.Select(TextOpDto.From).ToList(), Array.Empty<StructuredOpDto>(),
            ClientTag: Guid.NewGuid().ToString("n"));
        return await Connection.InvokeAsync<OperationAck>("SubmitOperation", envelope, ct);
    }

    /// <summary>Delete <paramref name="length"/> characters at <paramref name="start"/> and submit.</summary>
    public async Task<OperationAck> DeleteAsync(int start, int length, CancellationToken ct)
    {
        var ops = _text.BuildDelete(start, length);
        foreach (var op in ops) _text.Apply(op);
        var envelope = new OperationEnvelope(
            _documentId, ReplicaId, ops.Select(TextOpDto.From).ToList(), Array.Empty<StructuredOpDto>(),
            ClientTag: Guid.NewGuid().ToString("n"));
        return await Connection.InvokeAsync<OperationAck>("SubmitOperation", envelope, ct);
    }

    /// <summary>Set a structured field locally then submit (LWW).</summary>
    public async Task<OperationAck> SetFieldAsync(string itemId, string field, string value, CancellationToken ct, bool addItem = false)
    {
        var ops = new List<StructuredOperation>();
        if (addItem) ops.Add(StructuredOperation.AddItem(itemId, new LwwStamp(_clock.Tick(), ReplicaId)));
        ops.Add(StructuredOperation.SetField(itemId, field, value, new LwwStamp(_clock.Tick(), ReplicaId)));
        foreach (var op in ops) _structured.Apply(op);
        var envelope = new OperationEnvelope(
            _documentId, ReplicaId, Array.Empty<TextOpDto>(), ops.Select(StructuredOpDto.From).ToList(),
            ClientTag: Guid.NewGuid().ToString("n"));
        return await Connection.InvokeAsync<OperationAck>("SubmitOperation", envelope, ct);
    }

    /// <summary>Submit a raw envelope without touching local state (used for rate-limit/abuse tests).</summary>
    public Task<OperationAck> SubmitRawAsync(OperationEnvelope envelope, CancellationToken ct) =>
        Connection.InvokeAsync<OperationAck>("SubmitOperation", envelope, ct);

    public Task UpdatePresenceAsync(int cursorStart, int cursorEnd, CancellationToken ct) =>
        Connection.InvokeAsync("UpdatePresence", _documentId, new PresenceUpdate(cursorStart, cursorEnd, PresenceStatus.Active), ct);

    public Task<ResyncResult> ResyncAsync(long fromSequence, CancellationToken ct) =>
        Connection.InvokeAsync<ResyncResult>("ResyncDocument", _documentId, fromSequence, ct);

    private void ApplyRemote(OperationBroadcast broadcast)
    {
        if (_type == DocumentType.Text)
        {
            foreach (var dto in broadcast.TextOps)
            {
                var op = dto.ToDomain();
                _clock.Observe(op.Lamport);
                _text.Apply(op);
            }
        }
        else
        {
            foreach (var dto in broadcast.StructuredOps)
            {
                var op = dto.ToDomain();
                _clock.Observe(op.Stamp.Lamport);
                _structured.Apply(op);
            }
        }
        RemoteBatchCount++;
    }

    /// <summary>Apply a resync payload (checkpoint or missed operations) exactly like a real client.</summary>
    public void ApplyResync(ResyncResult resync)
    {
        if (resync.Full && resync.Checkpoint is not null)
        {
            SeedFrom(resync.Checkpoint);
            return;
        }
        foreach (var op in resync.Operations)
            ApplyRemote(op);
    }

    public string Text => _text.Materialize();
    public string StructuredJson => _structured.ToCanonicalJson();

    public async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int pollMs = 20)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Connection.DisposeAsync(); }
        catch { /* best-effort teardown */ }
    }
}
