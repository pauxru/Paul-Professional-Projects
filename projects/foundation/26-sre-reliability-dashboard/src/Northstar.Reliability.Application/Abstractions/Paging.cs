namespace Northstar.Reliability.Application.Abstractions;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);

    public static PageResult<T> Create(IEnumerable<T> source, int page, int pageSize)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var all = source.ToArray();
        return new PageResult<T>(
            all.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray(),
            safePage,
            safePageSize,
            all.Length);
    }
}
