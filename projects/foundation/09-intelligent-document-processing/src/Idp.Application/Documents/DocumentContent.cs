using Idp.Domain.Documents;

namespace Idp.Application.Documents;

/// <summary>One page of recognised words in the synthetic OCR coordinate space.</summary>
public sealed record OcrPage(int Page, double Width, double Height, IReadOnlyList<WordBox> Words);

/// <summary>
/// The parsed, layout-aware representation of a document that the classifier and extractor operate
/// on. <see cref="RawText"/> is always available; <see cref="Pages"/> carry word boxes for spatial
/// documents (the synthetic <c>.ocr.json</c> format). Plain text/CSV are given a synthesised layout.
/// </summary>
public sealed class DocumentContent
{
    public string FileName { get; }
    public string ContentType { get; }
    public string RawText { get; }
    public IReadOnlyList<OcrPage> Pages { get; }

    public DocumentContent(
        string fileName, string contentType, string rawText, IReadOnlyList<OcrPage> pages)
    {
        FileName = fileName;
        ContentType = contentType;
        RawText = rawText;
        Pages = pages;
    }

    public IReadOnlyList<WordBox> AllWords =>
        _allWords ??= Pages.SelectMany(p => p.Words).ToList();
    private IReadOnlyList<WordBox>? _allWords;

    public bool HasLayout => Pages.Any(p => p.Words.Count > 0);
}
