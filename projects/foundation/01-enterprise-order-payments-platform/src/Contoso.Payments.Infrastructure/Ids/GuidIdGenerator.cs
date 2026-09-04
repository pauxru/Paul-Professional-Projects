using Contoso.Payments.Application.Abstractions;

namespace Contoso.Payments.Infrastructure.Ids;

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewGuid() => Guid.NewGuid();
}
