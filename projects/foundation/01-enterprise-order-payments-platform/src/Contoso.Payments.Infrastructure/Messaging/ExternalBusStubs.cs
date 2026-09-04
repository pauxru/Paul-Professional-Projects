namespace Contoso.Payments.Infrastructure.Messaging;

/// <summary>
/// Non-implemented documented stub adapters for the messaging port.  The presence of these types
/// shows the composition switch is real; they intentionally throw at construction so nothing
/// silently starts against an un-configured broker.  Wire the real ones in a follow-up ADR.
/// </summary>
public sealed class RabbitMqEventBusStub : Contoso.Payments.Application.Abstractions.IEventBus
{
    public RabbitMqEventBusStub()
    {
        throw new NotSupportedException(
            "RabbitMq adapter is a documented stub in this reference implementation.  " +
            "Use Messaging:Provider=InMemory for local runs.");
    }

    public ValueTask PublishAsync(string topic, string payloadJson, CancellationToken ct)
        => throw new NotSupportedException();
}

public sealed class AzureServiceBusEventBusStub : Contoso.Payments.Application.Abstractions.IEventBus
{
    public AzureServiceBusEventBusStub()
    {
        throw new NotSupportedException(
            "Azure Service Bus adapter is a documented stub in this reference implementation.  " +
            "Use Messaging:Provider=InMemory for local runs.");
    }

    public ValueTask PublishAsync(string topic, string payloadJson, CancellationToken ct)
        => throw new NotSupportedException();
}
