namespace Contoso.Payments.Application.Abstractions;

public interface IIdGenerator
{
    Guid NewGuid();
}
