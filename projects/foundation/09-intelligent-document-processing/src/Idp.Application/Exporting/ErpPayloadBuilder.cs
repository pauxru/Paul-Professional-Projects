using System.Globalization;
using System.Text;
using System.Text.Json;
using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;

namespace Idp.Application.Exporting;

/// <summary>Serialises a document into the ERP export payload (JSON or CSV).</summary>
public static class ErpPayloadBuilder
{
    public static string Build(Document document, string format) =>
        format.Equals("Csv", StringComparison.OrdinalIgnoreCase)
            ? BuildCsv(document)
            : BuildJson(document);

    public static string BuildJson(Document document)
    {
        var payload = new
        {
            documentId = document.Id,
            type = document.DocumentType.ToString(),
            supplierId = document.SupplierId,
            supplierName = document.Text(FieldKeys.SupplierName),
            invoiceNumber = document.Text(FieldKeys.InvoiceNumber),
            invoiceDate = document.Text(FieldKeys.InvoiceDate),
            currency = document.Currency ?? document.Text(FieldKeys.Currency),
            subtotal = document.Amount(FieldKeys.Subtotal),
            tax = document.Amount(FieldKeys.Tax),
            total = document.Amount(FieldKeys.Total),
            poReference = document.Text(FieldKeys.PoReference),
            lines = document.LineItems
                .OrderBy(l => l.LineNumber)
                .Select(l => new
                {
                    l.LineNumber,
                    l.Description,
                    l.Quantity,
                    l.UnitPrice,
                    l.LineTotal,
                    l.TaxRate
                })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildCsv(Document document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("documentId,type,supplierName,invoiceNumber,invoiceDate,currency,total");
        sb.AppendLine(string.Join(",",
            Csv(document.Id.ToString()),
            Csv(document.DocumentType.ToString()),
            Csv(document.Text(FieldKeys.SupplierName)),
            Csv(document.Text(FieldKeys.InvoiceNumber)),
            Csv(document.Text(FieldKeys.InvoiceDate)),
            Csv(document.Currency ?? document.Text(FieldKeys.Currency)),
            Csv(document.Amount(FieldKeys.Total)?.ToString(CultureInfo.InvariantCulture))));
        return sb.ToString();
    }

    /// <summary>
    /// CSV field escaping that also neutralises CSV/formula injection: values beginning with a
    /// formula trigger (= + - @) are prefixed with a single quote so spreadsheet apps do not execute
    /// them.
    /// </summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var v = value;
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            v = "'" + v;
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            v = "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
