using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Fees;
using ExampleBank.Ledger.Domain.Monetary;
using ExampleBank.Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExampleBank.Ledger.Infrastructure.Seeding;

/// <summary>
/// Seeds the fictional Example Bank chart of accounts and fee schedules. Idempotent: it does
/// nothing if accounts already exist. Customer deposit accounts are created at runtime under the
/// per-currency <c>DEPOSITS-{CUR}</c> control account.
/// </summary>
public static class LedgerSeeder
{
    public static readonly string[] Currencies = { "KES", "USD", "EUR" };

    public static async Task SeedAsync(LedgerDbContext db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (await db.Accounts.AnyAsync(cancellationToken))
        {
            return;
        }

        var feeIncomeByCurrency = new Dictionary<string, Guid>();

        foreach (var code in Currencies)
        {
            var currency = Currency.FromCode(code);

            Add(db, now, $"CASH-{code}", $"Cash & vault ({code})", AccountType.Asset, currency);
            Add(db, now, WellKnownAccounts.FxClearing(code), $"FX clearing ({code})", AccountType.Asset, currency);
            var feeIncome = Add(db, now, $"FEE-INCOME-{code}", $"Fee income ({code})", AccountType.Income, currency);
            Add(db, now, $"INT-INCOME-{code}", $"Interest income ({code})", AccountType.Income, currency);
            Add(db, now, $"INT-EXPENSE-{code}", $"Interest expense ({code})", AccountType.Expense, currency);
            Add(db, now, WellKnownAccounts.FxGainLoss(code), $"FX rounding gain/loss ({code})", AccountType.Income, currency);
            Add(db, now, $"EQUITY-{code}", $"Retained earnings ({code})", AccountType.Equity, currency);
            Add(db, now, $"DEPOSITS-{code}", $"Customer deposits control ({code})", AccountType.Liability, currency, isControl: true);

            feeIncomeByCurrency[code] = feeIncome.Id;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Fee schedules referencing the seeded income accounts.
        db.FeeSchedules.Add(FeeSchedule.CreateFixed(
            "WIRE-FLAT-USD", "Flat wire fee", "USD", fixedAmountMinor: 500, feeIncomeByCurrency["USD"]));

        db.FeeSchedules.Add(FeeSchedule.CreatePercentage(
            "ATM-PCT-KES", "ATM withdrawal fee", "KES", rateBps: 150, feeIncomeByCurrency["KES"],
            minFeeMinor: 2000, maxFeeMinor: 50000));

        db.FeeSchedules.Add(FeeSchedule.CreateTiered(
            "TIER-USD", "Tiered transfer fee", "USD",
            new[]
            {
                FeeTier.Create(0, upToMinor: 100_000, rateBps: 100),
                FeeTier.Create(1, upToMinor: 1_000_000, rateBps: 50),
                FeeTier.Create(2, upToMinor: long.MaxValue, rateBps: 25),
            },
            feeIncomeByCurrency["USD"], minFeeMinor: 100, maxFeeMinor: 25_000));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Account Add(
        LedgerDbContext db,
        DateTimeOffset now,
        string code,
        string name,
        AccountType type,
        Currency currency,
        bool isControl = false)
    {
        var account = Account.Create(Guid.NewGuid(), code, name, type, currency, now, isControlAccount: isControl);
        db.Accounts.Add(account);
        return account;
    }
}
