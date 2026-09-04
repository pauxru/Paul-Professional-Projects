using System.Text.Json;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Domain.Search;
using EnterpriseSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseSearch.Infrastructure.Search;

public sealed class SqliteIndexStateStore(SearchDbContext database) : IIndexStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<IndexSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await database.Indices.AsNoTracking().OrderBy(index => index.Name).ToArrayAsync(cancellationToken);
        var documents = await database.Documents.AsNoTracking().ToArrayAsync(cancellationToken);
        return metadata.Select(index =>
        {
            var definition = JsonSerializer.Deserialize<IndexDefinition>(index.DefinitionJson, Json) ?? throw new InvalidOperationException($"Index '{index.Name}' has invalid persisted metadata.");
            var snapshots = documents.Where(document => string.Equals(document.IndexName, index.Name, StringComparison.OrdinalIgnoreCase))
                .Select(document => new IndexedDocumentSnapshot(JsonSerializer.Deserialize<SearchDocument>(document.PayloadJson, Json) ?? throw new InvalidOperationException($"Document '{document.DocumentId}' is invalid."), document.Version))
                .ToArray();
            return new IndexSnapshot(definition, snapshots, index.Generation, index.RefreshedAt);
        }).ToArray();
    }

    public async Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var metadata = await database.Indices.SingleOrDefaultAsync(index => index.Name == snapshot.Definition.Name, cancellationToken);
        if (metadata is null)
        {
            metadata = new SearchIndexEntity { Name = snapshot.Definition.Name };
            database.Indices.Add(metadata);
        }
        metadata.DefinitionJson = JsonSerializer.Serialize(snapshot.Definition, Json);
        metadata.Generation = snapshot.Generation;
        metadata.RefreshedAt = snapshot.RefreshedAt;
        var existing = await database.Documents.Where(document => document.IndexName == snapshot.Definition.Name).ToArrayAsync(cancellationToken);
        database.Documents.RemoveRange(existing);
        database.Documents.AddRange(snapshot.Documents.Select(document => new SearchDocumentEntity
        {
            IndexName = snapshot.Definition.Name,
            DocumentId = document.Document.Id,
            Version = document.Version,
            PayloadJson = JsonSerializer.Serialize(document.Document, Json)
        }));
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string indexName, CancellationToken cancellationToken = default)
    {
        var index = await database.Indices.SingleOrDefaultAsync(item => item.Name == indexName, cancellationToken);
        if (index is not null) database.Indices.Remove(index);
        database.Documents.RemoveRange(await database.Documents.Where(document => document.IndexName == indexName).ToArrayAsync(cancellationToken));
        database.Aliases.RemoveRange(await database.Aliases.Where(alias => alias.IndexName == indexName).ToArrayAsync(cancellationToken));
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAliasesAsync(CancellationToken cancellationToken = default) =>
        await database.Aliases.AsNoTracking().ToDictionaryAsync(alias => alias.Alias, alias => alias.IndexName, StringComparer.OrdinalIgnoreCase, cancellationToken);

    public async Task SaveAliasAsync(string alias, string indexName, CancellationToken cancellationToken = default)
    {
        var current = await database.Aliases.SingleOrDefaultAsync(item => item.Alias == alias, cancellationToken);
        if (current is null)
        {
            database.Aliases.Add(new SearchAliasEntity { Alias = alias, IndexName = indexName });
        }
        else
        {
            current.IndexName = indexName;
        }
        await database.SaveChangesAsync(cancellationToken);
    }
}
