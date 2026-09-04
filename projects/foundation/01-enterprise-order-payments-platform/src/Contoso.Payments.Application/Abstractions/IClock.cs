namespace Contoso.Payments.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
