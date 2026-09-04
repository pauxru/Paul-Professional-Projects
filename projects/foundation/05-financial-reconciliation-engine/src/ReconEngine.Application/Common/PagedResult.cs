namespace ReconEngine.Application.Common;

/// <summary>A page of results plus the paging metadata the API returns to clients.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Normalised paging request; page size is capped server-side.</summary>
public sealed record PageRequest(int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 500;

    public int NormalizedPage => Page < 1 ? 1 : Page;
    public int NormalizedPageSize => PageSize is < 1 or > MaxPageSize ? Math.Clamp(PageSize, 1, MaxPageSize) : PageSize;
    public int Skip => (NormalizedPage - 1) * NormalizedPageSize;
}
