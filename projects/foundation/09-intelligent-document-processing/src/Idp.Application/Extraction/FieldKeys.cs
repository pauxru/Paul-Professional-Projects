namespace Idp.Application.Extraction;

/// <summary>Canonical field keys used across extraction, validation and review.</summary>
public static class FieldKeys
{
    // Shared / invoice
    public const string SupplierName = "supplierName";
    public const string SupplierTaxId = "supplierTaxId";
    public const string InvoiceNumber = "invoiceNumber";
    public const string InvoiceDate = "invoiceDate";
    public const string DueDate = "dueDate";
    public const string Currency = "currency";
    public const string Subtotal = "subtotal";
    public const string Tax = "tax";
    public const string Total = "total";
    public const string PoReference = "poReference";
    public const string BankDetails = "bankDetails";

    // Purchase order
    public const string PoNumber = "poNumber";
    public const string PoDate = "poDate";
    public const string Buyer = "buyer";

    // Delivery note
    public const string DnNumber = "dnNumber";
    public const string DnDate = "dnDate";
}
