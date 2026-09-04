namespace Lab.Domain.Entities;

public sealed class LogisticsOrder
{
    private LogisticsOrder()
    {
    }

    public LogisticsOrder(Guid customerId, string reference, string destination, DateTimeOffset createdAt)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer is required.", nameof(customerId));
        }

        if (string.IsNullOrWhiteSpace(reference) || reference.Trim().Length is > 48)
        {
            throw new ArgumentException("Reference is required and must be at most 48 characters.", nameof(reference));
        }

        if (string.IsNullOrWhiteSpace(destination) || destination.Trim().Length is > 160)
        {
            throw new ArgumentException("Destination is required and must be at most 160 characters.", nameof(destination));
        }

        Id = Guid.NewGuid();
        CustomerId = customerId;
        Reference = reference.Trim().ToUpperInvariant();
        Destination = destination.Trim();
        CreatedAt = createdAt;
        Status = OrderStatus.Booked;
    }

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public string Destination { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public OrderStatus Status { get; private set; }

    public int Version { get; private set; }

    public Shipment? Shipment { get; private set; }

    public void Dispatch(string trackingNumber, DateTimeOffset dispatchedAt)
    {
        if (Status != OrderStatus.Booked)
        {
            throw new InvalidOperationException("Only booked orders can be dispatched.");
        }

        Shipment = new Shipment(Id, trackingNumber, dispatchedAt);
        Status = OrderStatus.InTransit;
    }

    public void MarkDelivered(DateTimeOffset deliveredAt)
    {
        if (Status != OrderStatus.InTransit || Shipment is null)
        {
            throw new InvalidOperationException("Only dispatched orders can be delivered.");
        }

        Shipment.MarkDelivered(deliveredAt);
        Status = OrderStatus.Delivered;
    }
}
