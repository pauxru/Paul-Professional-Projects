namespace Idp.Domain.Documents;

/// <summary>
/// A single recognised word with its layout box, in the synthetic OCR coordinate space.
/// Coordinates are in an abstract page unit (top-left origin, x grows right, y grows down).
/// This is the spatial primitive the extractor and table detector operate on.
/// </summary>
public readonly record struct WordBox(
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    int Page)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + (Width / 2.0);
    public double CenterY => Y + (Height / 2.0);

    /// <summary>True when this word's vertical centre sits on the same text line as another,
    /// within a tolerance expressed as a fraction of word height.</summary>
    public bool SharesRowWith(WordBox other, double tolerance = 0.6)
    {
        if (Page != other.Page) return false;
        var band = Math.Max(Height, other.Height) * tolerance;
        return Math.Abs(CenterY - other.CenterY) <= band;
    }
}
