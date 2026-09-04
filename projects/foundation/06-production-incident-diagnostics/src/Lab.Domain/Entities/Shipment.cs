namespace Lab.Domain.Entities;

public sealed class Shipment
{
    private Shipment()
    {
    }

    public Shipment(Guid orderId, string trackingNumber, DateTimeOffset dispatchedAt)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order is required.", nameof(orderId));
        }

        if (string.IsNullOrWhiteSpace(trackingNumber) || trackingNumber.Trim().Length is > 64)
        {
            throw new ArgumentException("Tracking number is required and must be at most 64 characters.", nameof(trackingNumber));
        }

        Id = Guid.NewGuid();
        OrderId = orderId;
        TrackingNumber = trackingNumber.Trim().ToUpperInvariant();
        DispatchedAt = dispatchedAt;
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string TrackingNumber { get; private set; } = string.Empty;

    public DateTimeOffset DispatchedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public void MarkDelivered(DateTimeOffset deliveredAt)
    {
        if (deliveredAt < DispatchedAt)
        {
            throw new ArgumentException("A shipment cannot be delivered before dispatch.", nameof(deliveredAt));
        }

        if (DeliveredAt is not null)
        {
            throw new InvalidOperationException("Shipment is already delivered.");
        }

        DeliveredAt = deliveredAt;
    }
}
