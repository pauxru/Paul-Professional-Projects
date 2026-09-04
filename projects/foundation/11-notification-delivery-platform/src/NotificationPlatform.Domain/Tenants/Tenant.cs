namespace NotificationPlatform.Domain.Tenants;

public sealed class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string DefaultLocale { get; private set; } = "en";
    public int MonthlyQuotaSoft { get; private set; }
    public int MonthlyQuotaHard { get; private set; }
    public double FairnessWeight { get; private set; } = 1.0;
    public DateTimeOffset CreatedAt { get; private set; }

    private Tenant() { }

    public Tenant(Guid id, string name, string slug, string defaultLocale, int softQuota, int hardQuota, double fairnessWeight, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Tenant id required", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tenant name required", nameof(name));
        if (string.IsNullOrWhiteSpace(slug)) throw new ArgumentException("Slug required", nameof(slug));
        if (softQuota < 0) throw new ArgumentException("softQuota must be non-negative", nameof(softQuota));
        if (hardQuota < softQuota) throw new ArgumentException("hardQuota must be >= softQuota", nameof(hardQuota));
        if (fairnessWeight <= 0) throw new ArgumentException("fairnessWeight must be positive", nameof(fairnessWeight));

        Id = id;
        Name = name;
        Slug = slug;
        DefaultLocale = defaultLocale;
        MonthlyQuotaSoft = softQuota;
        MonthlyQuotaHard = hardQuota;
        FairnessWeight = fairnessWeight;
        CreatedAt = createdAt;
    }
}
