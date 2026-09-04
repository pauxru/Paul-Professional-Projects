namespace Idp.Domain.Documents;

/// <summary>
/// A single extracted scalar field. Every field carries its value, a normalised value, a
/// confidence, the strategy that produced it and — crucially for human review — the source span/box
/// (the evidence) it was read from.
/// </summary>
public sealed class ExtractedField
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string FieldKey { get; private set; } = default!;
    public string? RawValue { get; private set; }
    public string? NormalizedValue { get; private set; }
    public double Confidence { get; private set; }
    public ExtractionStrategy Strategy { get; private set; }
    public string? SourceText { get; private set; }
    public bool IsRequired { get; private set; }

    public double? BoxX { get; private set; }
    public double? BoxY { get; private set; }
    public double? BoxWidth { get; private set; }
    public double? BoxHeight { get; private set; }
    public int? BoxPage { get; private set; }

    private ExtractedField() { }

    public ExtractedField(
        string fieldKey,
        string? rawValue,
        string? normalizedValue,
        double confidence,
        ExtractionStrategy strategy,
        bool isRequired,
        string? sourceText = null,
        WordBox? box = null)
    {
        Id = Guid.NewGuid();
        FieldKey = fieldKey;
        RawValue = rawValue;
        NormalizedValue = normalizedValue;
        Confidence = ConfidenceScoring.Clamp01(confidence);
        Strategy = strategy;
        IsRequired = isRequired;
        SourceText = sourceText;
        SetBox(box);
    }

    public WordBox? Box =>
        BoxX is { } x && BoxY is { } y && BoxWidth is { } w && BoxHeight is { } h && BoxPage is { } p
            ? new WordBox(SourceText ?? RawValue ?? string.Empty, x, y, w, h, p)
            : null;

    private void SetBox(WordBox? box)
    {
        if (box is null)
        {
            BoxX = BoxY = BoxWidth = BoxHeight = null;
            BoxPage = null;
            return;
        }
        var b = box.Value;
        BoxX = b.X; BoxY = b.Y; BoxWidth = b.Width; BoxHeight = b.Height; BoxPage = b.Page;
    }

    /// <summary>Apply a human correction: overwrite the value, set full confidence and record that
    /// the value now came from a reviewer rather than an extraction strategy.</summary>
    public void ApplyCorrection(string? newValue, string? normalizedValue)
    {
        RawValue = newValue;
        NormalizedValue = normalizedValue ?? newValue;
        Confidence = 1.0;
        Strategy = ExtractionStrategy.Derived;
    }

    internal void AttachTo(Guid documentId) => DocumentId = documentId;
}
