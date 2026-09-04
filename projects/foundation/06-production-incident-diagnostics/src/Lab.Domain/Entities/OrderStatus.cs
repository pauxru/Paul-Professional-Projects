namespace Lab.Domain.Entities;

public enum OrderStatus
{
    Draft = 0,
    Booked = 1,
    InTransit = 2,
    Delivered = 3,
    Cancelled = 4
}
