using Idp.Domain.Documents;
using Idp.Domain.Exports;
using Idp.Domain.Review;
using Idp.Domain.Suppliers;

namespace Idp.Api.Contracts;

// ---- Response DTOs -------------------------------------------------------------------------------

public sealed record DocumentSummaryDto(
    Guid Id, string FileName, string ContentType, DocumentType DocumentType, string State,
    string? Routing, double DocumentConfidence, decimal? DocumentValue, string? Currency,
    int Version, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record BoxDto(double X, double Y, double Width, double Height, int Page);

public sealed record FieldDto(
    string FieldKey, string? Value, string? NormalizedValue, double Confidence, string Strategy,
    bool IsRequired, string? Evidence, BoxDto? Box);

public sealed record LineItemDto(
    int LineNumber, string? Description, decimal? Quantity, decimal? UnitPrice, decimal? LineTotal,
    decimal? TaxRate, double Confidence);

public sealed record ValidationDto(
    string RuleName, string Outcome, string Message, IReadOnlyList<string> ImplicatedFields);

public sealed record TransitionDto(
    string FromState, string ToState, string Reason, string Actor, DateTime OccurredAtUtc);

public sealed record DocumentDetailDto(
    Guid Id, string FileName, string ContentType, DocumentType DocumentType, string State,
    string? Routing, double ClassificationConfidence, string? ClassificationExplanation,
    double DocumentConfidence, decimal? DocumentValue, string? Currency, Guid? SupplierId,
    string? SupplierNameRaw, int Version, string CorrelationId, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc, IReadOnlyList<FieldDto> Fields, IReadOnlyList<LineItemDto> LineItems,
    IReadOnlyList<ValidationDto> Validations, IReadOnlyList<TransitionDto> Transitions);

public sealed record ExportDto(
    Guid Id, Guid DocumentId, string Status, string Format, int Attempts, int MaxAttempts,
    string? ErpReference, string? OutboxPath, string? LastError, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SupplierDto(
    Guid Id, string Name, string? TaxId, string? DefaultCurrency, IReadOnlyList<string> Aliases,
    string? BankAccount, bool IsActive, int LearnedHintCount);

// ---- Request DTOs --------------------------------------------------------------------------------

public sealed record TokenRequest(string? Subject, string[]? Permissions);

public sealed record ClaimRequest(string? Reviewer);

public sealed record FieldCorrectionDto(string FieldKey, string? NewValue, string? Reason);

public sealed record CorrectRequest(string? Reviewer, List<FieldCorrectionDto> Corrections);

public sealed record ApproveRequest(string? Reviewer);

public sealed record RejectRequest(string? Reviewer, string? Reason);

// ---- Mapping -------------------------------------------------------------------------------------

public static class DtoMapper
{
    public static DocumentSummaryDto ToSummary(Document d) => new(
        d.Id, d.FileName, d.ContentType, d.DocumentType, d.State.ToString(),
        d.Routing?.ToString(), d.DocumentConfidence, d.DocumentValue, d.Currency, d.Version,
        d.CreatedAtUtc, d.UpdatedAtUtc);

    public static DocumentDetailDto ToDetail(Document d) => new(
        d.Id, d.FileName, d.ContentType, d.DocumentType, d.State.ToString(), d.Routing?.ToString(),
        d.ClassificationConfidence, d.ClassificationExplanation, d.DocumentConfidence,
        d.DocumentValue, d.Currency, d.SupplierId, d.SupplierNameRaw, d.Version, d.CorrelationId,
        d.CreatedAtUtc, d.UpdatedAtUtc,
        d.Fields.OrderBy(f => f.FieldKey).Select(ToField).ToList(),
        d.LineItems.OrderBy(l => l.LineNumber).Select(ToLineItem).ToList(),
        d.Validations.Select(ToValidation).ToList(),
        d.Transitions.OrderBy(t => t.OccurredAtUtc).Select(ToTransition).ToList());

    public static FieldDto ToField(ExtractedField f)
    {
        var box = f.Box;
        return new FieldDto(
            f.FieldKey, f.RawValue, f.NormalizedValue, f.Confidence, f.Strategy.ToString(),
            f.IsRequired, f.SourceText,
            box is { } b ? new BoxDto(b.X, b.Y, b.Width, b.Height, b.Page) : null);
    }

    public static LineItemDto ToLineItem(LineItem l) => new(
        l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.LineTotal, l.TaxRate, l.Confidence);

    public static ValidationDto ToValidation(DocumentValidation v) => new(
        v.RuleName, v.Outcome.ToString(), v.Message, v.ImplicatedFieldList);

    public static TransitionDto ToTransition(PipelineTransition t) => new(
        t.FromState.ToString(), t.ToState.ToString(), t.Reason, t.Actor, t.OccurredAtUtc);

    public static ExportDto ToExport(ExportRecord e) => new(
        e.Id, e.DocumentId, e.Status.ToString(), e.Format, e.Attempts, e.MaxAttempts,
        e.ErpReference, e.OutboxPath, e.LastError, e.CreatedAtUtc, e.UpdatedAtUtc);

    public static SupplierDto ToSupplier(Supplier s) => new(
        s.Id, s.Name, s.TaxId, s.DefaultCurrency, s.AliasList, s.BankAccount, s.IsActive,
        s.Hints.Count);
}
