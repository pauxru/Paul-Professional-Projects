using Collab.Api.Auth;
using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Application.Services;
using Collab.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Collab.Api.Realtime;

public static class HubGroups
{
    public static string ForDocument(Guid documentId) => $"doc:{documentId}";
}

/// <summary>
/// The realtime collaboration hub. Connections authenticate with a JWT (bearer or the
/// <c>access_token</c> query-string for browsers). Clients join per-document groups; the server is
/// authoritative for ordering and persistence and relays accepted operations, presence, comments and
/// resync payloads. All content authorization is enforced per call through the collaboration engine.
/// </summary>
[Authorize]
public sealed class CollaborationHub(
    CollaborationService collaboration,
    IPresenceStore presence,
    HubRateLimiter rateLimiter,
    ICollabMetrics metrics,
    IClock clock) : Hub
{
    public override Task OnConnectedAsync()
    {
        metrics.ClientConnected();
        rateLimiter.Register(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        metrics.ClientDisconnected();
        rateLimiter.Remove(Context.ConnectionId);

        var affected = presence.Disconnect(Context.ConnectionId);
        foreach (var documentId in affected)
            await BroadcastPresenceAsync(documentId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Join a document room; returns the authoritative current state for initial load.</summary>
    public async Task<DocumentStateDto> JoinDocument(Guid documentId)
    {
        var userId = Context.User!.GetUserId();
        DocumentStateDto state;
        try
        {
            state = await collaboration.GetStateAsync(userId, documentId, Context.ConnectionAborted);
        }
        catch (AppException ex)
        {
            throw new HubException(ex.Message);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ForDocument(documentId), Context.ConnectionAborted);
        presence.Join(documentId, Context.ConnectionId, userId, Context.User!.GetUserName(), clock.UtcNow);
        await BroadcastPresenceAsync(documentId);
        return state;
    }

    public async Task LeaveDocument(Guid documentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.ForDocument(documentId), Context.ConnectionAborted);
        presence.LeaveDocument(documentId, Context.ConnectionId);
        await BroadcastPresenceAsync(documentId);
    }

    /// <summary>
    /// Submit an atomic change set. Rate-limited per connection; returns an ack with the server
    /// sequence on success and raises a "Rejected" event plus a <see cref="HubException"/> otherwise,
    /// so the client always gets a well-defined result instead of silent divergence.
    /// </summary>
    public async Task<OperationAck> SubmitOperation(OperationEnvelope envelope)
    {
        var decision = rateLimiter.Acquire(Context.ConnectionId);
        if (decision != RateDecision.Allowed)
        {
            metrics.RecordRejected();
            var rejection = new OperationRejected(envelope.DocumentId, "rate_limited",
                "Operation rate limit exceeded.", envelope.ClientTag);
            await Clients.Caller.SendAsync("Rejected", rejection, Context.ConnectionAborted);
            if (decision == RateDecision.Disconnect)
                Context.Abort();
            throw new HubException("Operation rate limit exceeded.");
        }

        var userId = Context.User!.GetUserId();
        var result = await collaboration.ApplyAsync(userId, envelope, Context.ConnectionAborted);

        if (!result.Accepted)
        {
            await Clients.Caller.SendAsync("Rejected", result.Rejection, Context.ConnectionAborted);
            throw new HubException(result.Rejection!.Reason);
        }

        await Clients.OthersInGroup(HubGroups.ForDocument(envelope.DocumentId))
            .SendAsync("OperationApplied", result.Broadcast, Context.ConnectionAborted);

        return new OperationAck(envelope.DocumentId, result.Broadcast!.Sequence, envelope.ClientTag);
    }

    /// <summary>Update presence (cursor/selection/status). Broadcast is coalesced by the flusher.</summary>
    public Task UpdatePresence(Guid documentId, PresenceUpdate update)
    {
        presence.Update(documentId, Context.ConnectionId, update, clock.UtcNow);
        return Task.CompletedTask;
    }

    /// <summary>Liveness heartbeat; keeps this connection's presence active.</summary>
    public DateTimeOffset Ping()
    {
        presence.Heartbeat(Context.ConnectionId, clock.UtcNow);
        return clock.UtcNow;
    }

    /// <summary>
    /// Catch up after a disconnect: returns either a full checkpoint or the operations missed since
    /// <paramref name="fromSequence"/>. Also raised to the caller as a "Resynced" event.
    /// </summary>
    public async Task<ResyncResult> ResyncDocument(Guid documentId, long fromSequence)
    {
        var userId = Context.User!.GetUserId();
        ResyncResult result;
        try
        {
            result = await collaboration.ResyncAsync(userId, documentId, fromSequence, Context.ConnectionAborted);
        }
        catch (AppException ex)
        {
            throw new HubException(ex.Message);
        }

        await Clients.Caller.SendAsync("Resynced", result, Context.ConnectionAborted);
        return result;
    }

    private Task BroadcastPresenceAsync(Guid documentId)
    {
        var snapshot = new PresenceSnapshot(documentId, presence.Participants(documentId));
        return Clients.Group(HubGroups.ForDocument(documentId)).SendAsync("PresenceChanged", snapshot);
    }
}
