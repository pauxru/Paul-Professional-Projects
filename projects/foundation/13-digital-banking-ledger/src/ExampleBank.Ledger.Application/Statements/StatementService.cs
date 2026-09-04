using System.Globalization;
using System.Text;
using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;

namespace ExampleBank.Ledger.Application.Statements;

public sealed record StatementRequest(
    Guid AccountId,
    DateOnly FromDate,
    DateOnly ToDate,
    int Page = 1,
    int PageSize = 100);

/// <summary>
/// Produces period statements that tie out exactly: opening + Σ(signed movements) = closing.
/// Signed amounts follow the account's normal side (normal-side postings increase the balance).
/// </summary>
public sealed class StatementService
{
    private readonly ILedgerUnitOfWorkFactory _uowFactory;

    public StatementService(ILedgerUnitOfWorkFactory uowFactory) => _uowFactory = uowFactory;

    public async Task<StatementResult> GetAsync(StatementRequest request, CancellationToken cancellationToken)
    {
        if (request.ToDate < request.FromDate)
        {
            throw RequestValidationException.Single("toDate", "Statement 'toDate' must not be earlier than 'fromDate'.");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, PageRequest.MaxPageSize);

        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var account = await uow.Accounts.GetByIdAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.AccountId} not found.");

        var normalSide = account.NormalBalance;
        var (debitsBefore, creditsBefore) =
            await uow.Journal.SumPostingsForAccountBeforeAsync(request.AccountId, request.FromDate, cancellationToken);
        long opening = Signed(normalSide, debitsBefore, creditsBefore);

        var movements = await uow.Journal.GetAccountMovementsAsync(
            request.AccountId, request.FromDate, request.ToDate, cancellationToken);

        var lines = new List<StatementLine>(movements.Count);
        long running = opening;
        long totalMovements = 0;
        foreach (var m in movements)
        {
            long signed = SignedPosting(normalSide, m.Direction, m.AmountMinor);
            running += signed;
            totalMovements += signed;
            lines.Add(new StatementLine(
                m.EntryId, m.SequenceNumber, m.ValueDate, m.BookingTimestamp, m.Description, m.Reference,
                m.Direction, m.AmountMinor, signed, running));
        }

        long closing = opening + totalMovements;
        var pageLines = lines.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new StatementResult(
            account.Id, account.Code, account.Currency, request.FromDate, request.ToDate,
            opening, closing, totalMovements, pageLines, page, pageSize);
    }

    public static string ToCsv(StatementResult statement)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SequenceNumber,ValueDate,BookingTimestamp,Description,Reference,Direction,AmountMinor,SignedAmountMinor,RunningBalanceMinor");
        foreach (var l in statement.Lines)
        {
            sb.Append(l.SequenceNumber).Append(',')
              .Append(l.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(l.BookingTimestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(l.Description)).Append(',')
              .Append(Escape(l.Reference ?? string.Empty)).Append(',')
              .Append(l.Direction).Append(',')
              .Append(l.AmountMinor).Append(',')
              .Append(l.SignedAmountMinor).Append(',')
              .Append(l.RunningBalanceMinor).Append('\n');
        }

        return sb.ToString();
    }

    private static long Signed(NormalBalance normalSide, long debits, long credits) =>
        normalSide == NormalBalance.Debit ? debits - credits : credits - debits;

    private static long SignedPosting(NormalBalance normalSide, string direction, long amount)
    {
        bool isDebit = string.Equals(direction, "Debit", StringComparison.OrdinalIgnoreCase);
        bool onNormalSide = (normalSide == NormalBalance.Debit) == isDebit;
        return onNormalSide ? amount : -amount;
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
