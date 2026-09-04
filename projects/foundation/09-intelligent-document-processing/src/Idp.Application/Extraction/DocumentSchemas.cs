using Idp.Domain.Documents;

namespace Idp.Application.Extraction;

/// <summary>Per-document-type field schema: which fields are expected and which are required.</summary>
public static class DocumentSchemas
{
    public static readonly IReadOnlyDictionary<DocumentType, string[]> RequiredFields =
        new Dictionary<DocumentType, string[]>
        {
            [DocumentType.Invoice] = new[]
            {
                FieldKeys.SupplierName, FieldKeys.InvoiceNumber, FieldKeys.InvoiceDate,
                FieldKeys.Currency, FieldKeys.Total
            },
            [DocumentType.PurchaseOrder] = new[]
            {
                FieldKeys.PoNumber, FieldKeys.PoDate, FieldKeys.SupplierName, FieldKeys.Total
            },
            [DocumentType.DeliveryNote] = new[]
            {
                FieldKeys.DnNumber, FieldKeys.DnDate, FieldKeys.PoReference
            }
        };

    public static readonly IReadOnlyDictionary<DocumentType, string[]> OptionalFields =
        new Dictionary<DocumentType, string[]>
        {
            [DocumentType.Invoice] = new[]
            {
                FieldKeys.SupplierTaxId, FieldKeys.DueDate, FieldKeys.Subtotal, FieldKeys.Tax,
                FieldKeys.PoReference, FieldKeys.BankDetails
            },
            [DocumentType.PurchaseOrder] = new[] { FieldKeys.Buyer },
            [DocumentType.DeliveryNote] = Array.Empty<string>()
        };

    public static bool IsRequired(DocumentType type, string fieldKey) =>
        RequiredFields.TryGetValue(type, out var req) && req.Contains(fieldKey);

    public static IReadOnlyList<string> AllFields(DocumentType type)
    {
        var list = new List<string>();
        if (RequiredFields.TryGetValue(type, out var r)) list.AddRange(r);
        if (OptionalFields.TryGetValue(type, out var o)) list.AddRange(o);
        return list;
    }
}
