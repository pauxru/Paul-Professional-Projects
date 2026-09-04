namespace JobScheduler.Application.Abstractions;

/// <summary>Standard page envelope returned by list endpoints.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Normalised paging request with a server-side cap.</summary>
public readonly record struct PageRequest(int Page, int PageSize)
{
    public const int MaxPageSize = 200;

    public static PageRequest Of(int? page, int? pageSize)
    {
        int p = Math.Max(1, page ?? 1);
        int s = Math.Clamp(pageSize ?? 25, 1, MaxPageSize);
        return new PageRequest(p, s);
    }

    public int Skip => (Page - 1) * PageSize;
}
