using EnterpriseSearch.Application.Search;

namespace EnterpriseSearch.Infrastructure.Search;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
