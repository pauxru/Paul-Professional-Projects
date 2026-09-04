using Microsoft.EntityFrameworkCore;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Customer;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;

namespace ZeroTrust.Infrastructure;

public static class Seeder
{
    /// <summary>
    /// Idempotent demo data seeder. Fictional Northstar Financial Services scenario.
    /// </summary>
    public static async Task SeedAsync(ZeroTrustDbContext db, IClock clock, CancellationToken ct)
    {
        await db.Database.EnsureCreatedAsync(ct);

        if (!await db.SigningKeys.AnyAsync(ct))
        {
            var primary = JwksProvider.CreateRsaKey(isPrimary: true, notBefore: clock.UtcNow.AddMinutes(-1));
            db.SigningKeys.Add(primary);
        }

        if (!await db.Users.AnyAsync(ct))
        {
            var (aliceHash, aliceSalt) = SecretHasher.Hash("CustomerPassw0rd!");
            db.Users.Add(new User("alice", "alice@ntsf-demo.example", "Alice Kimani (fictional)",
                aliceHash, aliceSalt, "customer", mfaEnrolled: false));

            var (bobHash, bobSalt) = SecretHasher.Hash("CustomerPassw0rd!");
            db.Users.Add(new User("bob", "bob@ntsf-demo.example", "Bob Otieno (fictional)",
                bobHash, bobSalt, "customer", mfaEnrolled: false));

            var (adminHash, adminSalt) = SecretHasher.Hash("AdminPassw0rd!");
            db.Users.Add(new User("admin", "admin@ntsf-demo.example", "Admin Ops (fictional)",
                adminHash, adminSalt, "admin,customer", mfaEnrolled: true));
        }

        if (!await db.Partners.AnyAsync(ct))
        {
            var (secHash, secSalt) = SecretHasher.Hash("PartnerSecret!ExampleOnly");
            db.Partners.Add(new Partner(
                partnerCode: "ACME-TREASURY",
                displayName: "Acme Treasury Ltd (fictional)",
                clientId: "acme-treasury-client",
                clientSecretHash: secHash,
                clientSecretSalt: secSalt,
                allowedScopes: $"{Scope.PartnerPaymentsInitiate} {Scope.PartnerPaymentsRead} {Scope.PartnerStatementsRead}",
                allowedIps: "*",
                clientCertThumbprint: "AA11BB22CC33DD44EE55FF66AA11BB22CC33DD44",
                rateLimit: 60,
                enabled: true));

            var (sec2Hash, sec2Salt) = SecretHasher.Hash("PartnerSecret!ExampleOnly2");
            db.Partners.Add(new Partner(
                partnerCode: "SAVANNA-LOGISTICS",
                displayName: "Savanna Logistics Ltd (fictional)",
                clientId: "savanna-client",
                clientSecretHash: sec2Hash,
                clientSecretSalt: sec2Salt,
                allowedScopes: $"{Scope.PartnerPaymentsRead}",
                allowedIps: "127.0.0.1,::1",
                clientCertThumbprint: "11AA22BB33CC44DD55EE66FF77AA88BB99CC00DD",
                rateLimit: 20,
                enabled: true));
        }

        if (!await db.ApiKeys.AnyAsync(ct))
        {
            var (h, s) = SecretHasher.Hash("legacy-key-plaintext-example");
            db.ApiKeys.Add(new ApiKey("acme-legacy-key-01", h, s, "ACME-TREASURY",
                Scope.PartnerPaymentsRead, deprecatedAfterUtc: clock.UtcNow.AddDays(30)));
        }

        if (!await db.Accounts.AnyAsync(ct))
        {
            db.Accounts.Add(new Account("NTSF-0001-ALICE", "alice", "Everyday Checking", "USD", 3_450_00));
            db.Accounts.Add(new Account("NTSF-0002-ALICE", "alice", "Savings", "USD", 12_500_00));
            db.Accounts.Add(new Account("NTSF-0003-BOB", "bob", "Everyday Checking", "USD", 875_00));
        }

        await db.SaveChangesAsync(ct);

        if (!await db.Statements.AnyAsync(ct))
        {
            var accts = await db.Accounts.ToListAsync(ct);
            foreach (var acct in accts)
            {
                db.Statements.Add(new Statement(acct.Id, acct.OwnerSubject,
                    clock.UtcNow.Year, Math.Max(1, clock.UtcNow.Month - 1),
                    acct.BalanceMinorUnits, acct.BalanceMinorUnits, acct.Currency));
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
