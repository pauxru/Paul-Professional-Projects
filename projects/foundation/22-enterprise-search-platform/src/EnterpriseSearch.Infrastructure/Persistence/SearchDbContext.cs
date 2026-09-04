using Microsoft.EntityFrameworkCore;

namespace EnterpriseSearch.Infrastructure.Persistence;

public sealed class SearchDbContext(DbContextOptions<SearchDbContext> options) : DbContext(options)
{
    public DbSet<SearchIndexEntity> Indices => Set<SearchIndexEntity>();
    public DbSet<SearchDocumentEntity> Documents => Set<SearchDocumentEntity>();
    public DbSet<SearchAliasEntity> Aliases => Set<SearchAliasEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SearchIndexEntity>(entity =>
        {
            entity.ToTable("search_indices");
            entity.HasKey(item => item.Name);
            entity.Property(item => item.Name).HasMaxLength(160);
            entity.Property(item => item.DefinitionJson).IsRequired();
            entity.HasIndex(item => item.RefreshedAt);
        });
        modelBuilder.Entity<SearchDocumentEntity>(entity =>
        {
            entity.ToTable("search_documents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.IndexName).HasMaxLength(160).IsRequired();
            entity.Property(item => item.DocumentId).HasMaxLength(256).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.HasIndex(item => new { item.IndexName, item.DocumentId }).IsUnique();
            entity.HasIndex(item => item.IndexName);
        });
        modelBuilder.Entity<SearchAliasEntity>(entity =>
        {
            entity.ToTable("search_aliases");
            entity.HasKey(item => item.Alias);
            entity.Property(item => item.Alias).HasMaxLength(160);
            entity.Property(item => item.IndexName).HasMaxLength(160).IsRequired();
            entity.HasIndex(item => item.IndexName);
        });
    }
}

public sealed class SearchIndexEntity
{
    public string Name { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public long Generation { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
}

public sealed class SearchDocumentEntity
{
    public long Id { get; set; }
    public string IndexName { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class SearchAliasEntity
{
    public string Alias { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
}
