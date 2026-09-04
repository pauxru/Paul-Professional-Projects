using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idp.Application.Documents;
using Idp.Domain.Documents;

namespace Idp.Infrastructure.Extraction.Parsing;

/// <summary>
/// Parses the synthetic OCR coordinate format (<c>.ocr.json</c>) into a layout-aware
/// <see cref="DocumentContent"/>. This is the format that lets the pipeline be genuinely spatial
/// without a real OCR/PDF dependency: it carries per-word boxes with page coordinates.
/// </summary>
public sealed class OcrJsonParser : IDocumentParser
{
    public const string ContentTypeValue = "application/vnd.idp.ocr+json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public bool CanParse(string fileName, string contentType) =>
        fileName.EndsWith(".ocr.json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, ContentTypeValue, StringComparison.OrdinalIgnoreCase);

    public DocumentContent Parse(string fileName, string contentType, byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        var dto = JsonSerializer.Deserialize<OcrDocumentDto>(json, JsonOptions)
                  ?? new OcrDocumentDto();

        var pages = new List<OcrPage>();
        var dtoPages = dto.Pages ?? new List<OcrPageDto>();
        foreach (var p in dtoPages)
        {
            var words = (p.Words ?? new List<OcrWordDto>())
                .Where(w => !string.IsNullOrEmpty(w.Text))
                .Select(w => new WordBox(w.Text!, w.X, w.Y, w.W, w.H, p.Page <= 0 ? 1 : p.Page))
                .ToList();
            pages.Add(new OcrPage(
                p.Page <= 0 ? 1 : p.Page,
                p.Width <= 0 ? 1000 : p.Width,
                p.Height <= 0 ? 1400 : p.Height,
                words));
        }

        if (pages.Count == 0)
            pages.Add(new OcrPage(1, 1000, 1400, Array.Empty<WordBox>()));

        var rawText = LayoutSynthesizer.ComposeText(pages);
        return new DocumentContent(fileName, contentType, rawText, pages);
    }

    private sealed class OcrDocumentDto
    {
        public List<OcrPageDto>? Pages { get; set; }
    }

    private sealed class OcrPageDto
    {
        public int Page { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public List<OcrWordDto>? Words { get; set; }
    }

    private sealed class OcrWordDto
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("w")] public double W { get; set; }
        [JsonPropertyName("h")] public double H { get; set; }
    }
}
