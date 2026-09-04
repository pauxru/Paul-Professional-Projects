using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Domain.Inventory;

/// <summary>
/// Stock for a single SKU.  Reservations are held on top of the physical count so that a
/// concurrent Add-to-Cart-Twice race cannot oversell.  Commit turns a reservation into a
/// deduction from on-hand stock (fulfilment); Release returns it to the pool (cancellation).
/// </summary>
public sealed class InventoryItem : AggregateRoot
{
    private readonly List<StockReservation> _reservations = new();

    private InventoryItem() { Sku = string.Empty; }

    public InventoryItem(Guid productId, string sku, int onHand)
    {
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        if (string.IsNullOrWhiteSpace(sku)) throw new DomainException("SKU is required.");
        if (onHand < 0) throw new DomainException("OnHand cannot be negative.");
        ProductId = productId;
        Sku = sku.Trim().ToUpperInvariant();
        OnHand = onHand;
        Version = 1;
    }

    public Guid ProductId { get; private set; }
    public string Sku { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved => _reservations.Where(r => r.State == ReservationState.Held).Sum(r => r.Quantity);
    public int Available => OnHand - Reserved;
    public IReadOnlyCollection<StockReservation> Reservations => _reservations;

    /// <summary>
    /// Attempt to hold <paramref name="quantity"/> units against the given order.  Returns a
    /// reservation id on success.  Throws <see cref="DomainException"/> when the SKU is out of
    /// stock — callers must convert this to a 409/422 at the API edge.
    /// </summary>
    public Guid Reserve(Guid orderId, int quantity)
    {
        if (quantity <= 0) throw new DomainException("Reservation quantity must be positive.");
        if (Available < quantity)
            throw new DomainException($"Insufficient stock for SKU {Sku}.  Available={Available}, requested={quantity}.", "inventory.oversell_prevented");

        var reservation = new StockReservation(Guid.NewGuid(), orderId, quantity, ReservationState.Held);
        _reservations.Add(reservation);
        Version++;
        return reservation.Id;
    }

    public void Release(Guid reservationId)
    {
        var r = _reservations.FirstOrDefault(x => x.Id == reservationId);
        if (r is null) throw new DomainException("Reservation not found.", "inventory.reservation_not_found");
        if (r.State != ReservationState.Held) return; // idempotent
        r.State = ReservationState.Released;
        Version++;
    }

    public void Commit(Guid reservationId)
    {
        var r = _reservations.FirstOrDefault(x => x.Id == reservationId);
        if (r is null) throw new DomainException("Reservation not found.", "inventory.reservation_not_found");
        if (r.State == ReservationState.Committed) return; // idempotent
        if (r.State != ReservationState.Held)
            throw new DomainException("Only held reservations can be committed.");
        r.State = ReservationState.Committed;
        OnHand -= r.Quantity;
        Version++;
    }

    public void Restock(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Restock quantity must be positive.");
        OnHand += quantity;
        Version++;
    }
}

public enum ReservationState
{
    Held = 0,
    Committed = 1,
    Released = 2
}

public sealed class StockReservation
{
    private StockReservation() { }

    public StockReservation(Guid id, Guid orderId, int quantity, ReservationState state)
    {
        Id = id;
        OrderId = orderId;
        Quantity = quantity;
        State = state;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public int Quantity { get; private set; }
    public ReservationState State { get; internal set; }
}
