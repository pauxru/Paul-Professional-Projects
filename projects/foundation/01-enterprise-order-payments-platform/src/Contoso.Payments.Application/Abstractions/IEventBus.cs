namespace Contoso.Payments.Application.Abstractions;

/// <summary>
/// In-process publish port for domain events.  The default adapter is a
/// <c>System.Threading.Channels</c>-backed pump.  RabbitMQ / Azure Service Bus adapters live
/// in Infrastructure and are opt-in via configuration.
/// </summary>
public interface IEventBus
{
    ValueTask PublishAsync(string topic, string payloadJson, CancellationToken ct);
}
