using System.Text;
using Idp.Application.Documents;

namespace Idp.Infrastructure.Extraction.Parsing;

/// <summary>Parses plain text (<c>.txt</c>) and gives it a synthesised monospaced layout.</summary>
public sealed class PlainTextParser : IDocumentParser
{
    public bool CanParse(string fileName, string contentType)
    {
        if (fileName.EndsWith(".ocr.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        return fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(contentType, "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    public DocumentContent Parse(string fileName, string contentType, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var pages = LayoutSynthesizer.FromPlainText(text);
        return new DocumentContent(fileName, contentType, text, pages);
    }
}

/// <summary>Parses CSV (<c>.csv</c>) into an aligned grid layout so the table detector can run on it.</summary>
public sealed class CsvParser : IDocumentParser
{
    public bool CanParse(string fileName, string contentType) =>
        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "text/csv", StringComparison.OrdinalIgnoreCase);

    public DocumentContent Parse(string fileName, string contentType, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var pages = LayoutSynthesizer.FromCsv(text, out var rawText);
        return new DocumentContent(fileName, contentType, rawText, pages);
    }
}
