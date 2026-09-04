using System.Collections.Concurrent;

namespace Idp.Infrastructure.Exporting;

/// <summary>A booking accepted by the simulated ERP.</summary>
public sealed record ErpBooking(
    string IdempotencyKey, Guid DocumentId, string Reference, string Payload, DateTime BookedAtUtc);

/// <summary>
/// Shared, in-process state for the simulated ERP. Both the in-process <see cref="SimulatedErpExportClient"/>
/// and the HTTP <c>/simulated-erp/invoices</c> endpoint use this ledger, so the idempotency guarantee
/// holds whichever path an export takes. Registered as a singleton.
/// </summary>
public sealed class SimulatedErpLedger
{
    private readonly ConcurrentDictionary<string, ErpBooking> _bookings = new(StringComparer.Ordinal);

    /// <summary>Book a document idempotently. Returns the (possibly pre-existing) booking and whether
    /// it already existed for this idempotency key.</summary>
    public (ErpBooking Booking, bool AlreadyExisted) Book(
        string idempotencyKey, Guid documentId, string payload, DateTime nowUtc)
    {
        var existed = true;
        var booking = _bookings.GetOrAdd(idempotencyKey, key =>
        {
            existed = false;
            return new ErpBooking(
                key, documentId, $"ERP-{Guid.NewGuid():N}"[..12], payload, nowUtc);
        });
        return (booking, existed);
    }

    public bool TryGet(string idempotencyKey, out ErpBooking? booking)
    {
        var found = _bookings.TryGetValue(idempotencyKey, out var b);
        booking = b;
        return found;
    }

    public IReadOnlyCollection<ErpBooking> All() => _bookings.Values.ToList();

    public int Count => _bookings.Count;
}
