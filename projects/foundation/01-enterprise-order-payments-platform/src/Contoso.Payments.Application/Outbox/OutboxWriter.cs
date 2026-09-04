using System.Text.Json;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Application.Outbox;

/// <summary>
/// Helper that maps the domain events raised on an aggregate into <see cref="OutboxMessage"/>
/// rows.  The rows are added to the DbContext but not saved — the caller commits the SAME
/// transaction that persists the aggregate change, giving us atomic "state + event".
/// </summary>
public sealed class OutboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAppDbContext _db;
    private readonly IClock _clock;

    public OutboxWriter(IAppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Enqueue(IEnumerable<DomainEvent> events, string correlationId)
    {
        foreach (var ev in events)
        {
            var topic = ev.GetType().Name;
            var payload = JsonSerializer.Serialize((object)ev, ev.GetType(), JsonOptions);
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = ev.EventId,
                Topic = topic,
                PayloadJson = payload,
                OccurredAtUtc = ev.OccurredAtUtc,
                NextAttemptAtUtc = _clock.UtcNow,
                Attempts = 0,
                Dispatched = false,
                CorrelationId = correlationId
            });
        }
    }
}
