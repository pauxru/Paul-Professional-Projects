namespace NotificationPlatform.Infrastructure.Seed;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Domain.Preferences;
using NotificationPlatform.Domain.Recipients;
using NotificationPlatform.Domain.Suppressions;
using NotificationPlatform.Domain.Templates;
using NotificationPlatform.Domain.Tenants;
using NotificationPlatform.Infrastructure.Persistence;

public static class SeedData
{
    // Well-known IDs make integration tests deterministic and demo scripts scriptable.
    public static readonly Guid ContosoTenantId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SavannaTenantId = new("22222222-2222-2222-2222-222222222222");

    public static async Task EnsureSeedAsync(AppDbContext db, IClock clock, IIdGenerator ids, ILogger logger, CancellationToken ct)
    {
        if (await db.Tenants.AnyAsync(ct).ConfigureAwait(false)) return;
        logger.LogInformation("Seeding demo data (fictional tenants: Contoso Retail, Savanna Logistics Ltd)");
        var now = clock.UtcNow;

        var contoso = new Tenant(ContosoTenantId, "Contoso Retail", "contoso", "en-US", 50_000, 100_000, 1.0, now);
        var savanna = new Tenant(SavannaTenantId, "Savanna Logistics Ltd (fictional)", "savanna", "en-KE", 10_000, 25_000, 1.5, now);
        db.Tenants.AddRange(contoso, savanna);

        var alice = new Recipient(ids.NewId(), contoso.Id, "cust-001", "alice@contoso.example", "+15551234567", "push-token-alice", null, "en-US", "America/New_York", TimeSpan.FromHours(22), TimeSpan.FromHours(7), "Alice", "Nguyen");
        var bob = new Recipient(ids.NewId(), contoso.Id, "cust-002", "bob@contoso.example", "+15559876543", null, null, "fr-FR", "Europe/Paris", TimeSpan.FromHours(22), TimeSpan.FromHours(6), "Bob", "Martin");
        var wanjiku = new Recipient(ids.NewId(), savanna.Id, "cust-501", "wanjiku@example.co.ke", "+254712345678", null, "https://webhooks.example.com/savanna", "sw-KE", "Africa/Nairobi", TimeSpan.FromHours(21), TimeSpan.FromHours(6), "Wanjiku", "Kamau");
        var otieno = new Recipient(ids.NewId(), savanna.Id, "cust-502", "otieno@example.co.ke", "+254798765432", null, null, "en-KE", "Africa/Nairobi", TimeSpan.Zero, TimeSpan.Zero, "Otieno", "Odhiambo");
        db.Recipients.AddRange(alice, bob, wanjiku, otieno);

        db.Templates.AddRange(
            new NotificationTemplate(ids.NewId(), contoso.Id, "order.confirmation", NotificationChannel.Email, "en-US", 1,
                "Order #{{order.id}} confirmed",
                "Hi {{user.firstName}}, your order for {{order.item}} totalling {{order.total}} has been confirmed.",
                NotificationCategory.Transactional, true, true, now),
            new NotificationTemplate(ids.NewId(), contoso.Id, "order.confirmation", NotificationChannel.Sms, "en-US", 1,
                null, "Contoso: order #{{order.id}} confirmed. Total {{order.total}}.",
                NotificationCategory.Transactional, true, true, now),
            new NotificationTemplate(ids.NewId(), contoso.Id, "order.confirmation", NotificationChannel.Email, "fr-FR", 1,
                "Confirmation de la commande n°{{order.id}}",
                "Bonjour {{user.firstName}}, votre commande pour {{order.item}} d'un montant de {{order.total}} est confirmée.",
                NotificationCategory.Transactional, true, true, now),
            new NotificationTemplate(ids.NewId(), savanna.Id, "shipment.dispatched", NotificationChannel.Sms, "sw-KE", 1,
                null, "Habari {{user.firstName}}, mzigo wako #{{shipment.id}} umetumwa.",
                NotificationCategory.Transactional, true, true, now),
            new NotificationTemplate(ids.NewId(), savanna.Id, "shipment.dispatched", NotificationChannel.Sms, "en-KE", 1,
                null, "Hi {{user.firstName}}, your shipment #{{shipment.id}} has been dispatched.",
                NotificationCategory.Transactional, true, true, now),
            new NotificationTemplate(ids.NewId(), savanna.Id, "shipment.dispatched", NotificationChannel.Webhook, "en", 1,
                null, "{\"event\":\"shipment.dispatched\",\"shipmentId\":\"{{shipment.id}}\",\"recipient\":\"{{user.externalId}}\"}",
                NotificationCategory.System, true, true, now),
            new NotificationTemplate(ids.NewId(), contoso.Id, "marketing.weekly_offer", NotificationChannel.Email, "en-US", 1,
                "This week's picks for you",
                "Hi {{user.firstName}}!\n{{#each offers}}- {{.}}\n{{/each}}",
                NotificationCategory.Marketing, true, true, now),
            new NotificationTemplate(ids.NewId(), contoso.Id, "marketing.weekly_offer", NotificationChannel.Push, "en-US", 1,
                null, "Hi {{user.firstName}}, {{offer.count}} new offers waiting.",
                NotificationCategory.Marketing, true, true, now)
        );

        db.Preferences.AddRange(
            new RecipientPreference(ids.NewId(), contoso.Id, alice.Id, NotificationChannel.Email, NotificationCategory.Marketing, true, now),
            new RecipientPreference(ids.NewId(), contoso.Id, alice.Id, NotificationChannel.Email, NotificationCategory.Transactional, true, now),
            new RecipientPreference(ids.NewId(), contoso.Id, bob.Id, NotificationChannel.Email, NotificationCategory.Marketing, false, now),
            new RecipientPreference(ids.NewId(), savanna.Id, wanjiku.Id, NotificationChannel.Sms, NotificationCategory.Transactional, true, now)
        );

        db.Suppressions.Add(new SuppressionEntry(ids.NewId(), contoso.Id, NotificationChannel.Email, "blocked@contoso.example", SuppressionReason.HardBounce, "seeded example", now));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
