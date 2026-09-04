namespace ExampleBank.Ledger.Application.Common;

/// <summary>A page of results with the paging metadata the API contract requires.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

/// <summary>Normalised, server-capped paging parameters.</summary>
public sealed record PageRequest
{
    public const int MaxPageSize = 200;

    public int Page { get; }
    public int PageSize { get; }

    public PageRequest(int page, int pageSize)
    {
        Page = page < 1 ? 1 : page;
        PageSize = pageSize switch
        {
            < 1 => 20,
            > MaxPageSize => MaxPageSize,
            _ => pageSize,
        };
    }

    public int Skip => (Page - 1) * PageSize;
}
