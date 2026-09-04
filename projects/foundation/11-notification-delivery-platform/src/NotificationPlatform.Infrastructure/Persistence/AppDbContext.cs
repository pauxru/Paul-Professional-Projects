namespace NotificationPlatform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Domain.Preferences;
using NotificationPlatform.Domain.Providers;
using NotificationPlatform.Domain.Recipients;
using NotificationPlatform.Domain.Suppressions;
using NotificationPlatform.Domain.Templates;
using NotificationPlatform.Domain.Tenants;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<NotificationTemplate> Templates => Set<NotificationTemplate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationAttempt> Attempts => Set<NotificationAttempt>();
    public DbSet<IdempotencyRecord> Idempotency => Set<IdempotencyRecord>();
    public DbSet<DeliveryReceipt> Receipts => Set<DeliveryReceipt>();
    public DbSet<RecipientPreference> Preferences => Set<RecipientPreference>();
    public DbSet<SuppressionEntry> Suppressions => Set<SuppressionEntry>();
    public DbSet<ProviderHealth> ProviderHealth => Set<ProviderHealth>();
    public DbSet<TenantUsageCounter> UsageCounters => Set<TenantUsageCounter>();
    public DbSet<RecipientFrequencyCounter> FrequencyCounters => Set<RecipientFrequencyCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // SQLite: store DateTimeOffset as long ticks so comparisons translate cleanly.
        var dtoToTicks = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableDtoToTicks = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
        // TimeSpan is stored as ticks (long) already for SQLite by default? Force to be safe.
        var tsToTicks = new ValueConverter<TimeSpan, long>(
            v => v.Ticks,
            v => TimeSpan.FromTicks(v));
        var nullableTsToTicks = new ValueConverter<TimeSpan?, long?>(
            v => v.HasValue ? v.Value.Ticks : null,
            v => v.HasValue ? TimeSpan.FromTicks(v.Value) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(dtoToTicks);
                else if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(nullableDtoToTicks);
                else if (property.ClrType == typeof(TimeSpan))
                    property.SetValueConverter(tsToTicks);
                else if (property.ClrType == typeof(TimeSpan?))
                    property.SetValueConverter(nullableTsToTicks);
            }
        }
    }
}

