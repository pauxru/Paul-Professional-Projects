using System.Collections.Concurrent;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Infrastructure.Query;

/// <summary>
/// Simple, purpose-built inverted index over the SearchIndex table. Tokenisation is
/// lowercase, alpha-numeric, split on any non-word character. Cheap, predictable, and good
/// enough for the audit-log free-text needs. See ADR-002 for the "why not FTS5" reasoning.
/// </summary>
public sealed class InvertedSearchIndex : ISearchIndex
{
    private readonly AppDbContext _db;
    public InvertedSearchIndex(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Guid>> SearchAsync(string tenantId, string text, int limit, CancellationToken ct)
    {
        var tokens = Tokenise(text).Distinct().ToList();
        if (tokens.Count == 0) return Array.Empty<Guid>();

        var query = _db.SearchIndex.AsNoTracking().Where(x => x.TenantId == tenantId);
        foreach (var t in tokens)
        {
            var like = "% " + t + " %";
            var like2 = t + " %";
            var like3 = "% " + t;
            var like4 = t;
            query = query.Where(x => EF.Functions.Like(x.Text, like) || EF.Functions.Like(x.Text, like2) || EF.Functions.Like(x.Text, like3) || EF.Functions.Like(x.Text, like4));
        }
        return await query.Take(limit).Select(x => x.EventId).ToListAsync(ct);
    }

    public Task IndexAsync(Guid eventId, string tenantId, string text, CancellationToken ct)
    {
        var tokenised = string.Join(' ', Tokenise(text));
        _db.SearchIndex.Add(new SearchIndexEntry { EventId = eventId, TenantId = tenantId, Text = tokenised });
        return _db.SaveChangesAsync(ct);
    }

    private static IEnumerable<string> Tokenise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
