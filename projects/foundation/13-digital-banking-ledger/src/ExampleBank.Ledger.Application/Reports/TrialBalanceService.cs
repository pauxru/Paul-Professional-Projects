using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;

namespace ExampleBank.Ledger.Application.Reports;

/// <summary>Builds the trial balance and asserts it balances to zero in every currency.</summary>
public sealed class TrialBalanceService
{
    private readonly ILedgerUnitOfWorkFactory _uowFactory;

    public TrialBalanceService(ILedgerUnitOfWorkFactory uowFactory) => _uowFactory = uowFactory;

    public async Task<TrialBalanceResult> GetAsync(CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var accounts = await uow.Accounts.ListAsync(cancellationToken);

        var lines = new List<TrialBalanceLine>(accounts.Count);
        foreach (var a in accounts.Where(a => !a.IsControlAccount).OrderBy(a => a.Code))
        {
            long debitSigned = a.DebitSignedBalanceMinor; // debits - credits
            long debitBalance = debitSigned > 0 ? debitSigned : 0;
            long creditBalance = debitSigned < 0 ? -debitSigned : 0;
            lines.Add(new TrialBalanceLine(
                a.Id, a.Code, a.Name, a.Type.ToString(), a.Currency,
                a.TotalDebitsMinor, a.TotalCreditsMinor, debitBalance, creditBalance));
        }

        var totals = lines
            .GroupBy(l => l.Currency)
            .Select(g =>
            {
                long debits = g.Sum(l => l.TotalDebitsMinor);
                long credits = g.Sum(l => l.TotalCreditsMinor);
                return new TrialBalanceCurrencyTotal(g.Key, debits, credits, debits == credits);
            })
            .OrderBy(t => t.Currency)
            .ToList();

        return new TrialBalanceResult(lines, totals, totals.All(t => t.IsBalanced));
    }
}
