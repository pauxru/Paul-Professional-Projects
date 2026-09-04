using ReconEngine.Domain.Enums;
using ReconEngine.Domain.Normalization;

namespace ReconEngine.Application.Ingestion;

public enum RecordFileFormat
{
    Csv = 0,
    FixedWidth = 1,
}

/// <summary>The logical fields the engine understands. A profile maps file columns onto these.</summary>
public enum RecordField
{
    Reference,
    CounterpartyReference,
    Amount,
    Currency,
    Date,
    Status,
    Fee,
}

/// <summary>
/// Locates one logical field within a file. For CSV, either <see cref="ColumnIndex"/> or
/// <see cref="Header"/> is used; for fixed-width, <see cref="Start"/> and <see cref="Length"/>.
/// </summary>
public sealed record FieldSpec(
    RecordField Field,
    int? ColumnIndex = null,
    string? Header = null,
    int? Start = null,
    int? Length = null);

/// <summary>
/// A declarative, data-driven description of a file's shape: delimiter, header, date formats, decimal
/// separator, sign convention, minor-vs-major units, source timezone offset and the column→field map.
/// New file layouts are onboarded by adding a profile, never by changing parser code.
/// </summary>
public sealed record FileFormatProfile
{
    public required string Name { get; init; }
    public required RecordFileFormat Format { get; init; }
    public required RecordSource Source { get; init; }

    public bool HasHeader { get; init; } = true;
    public char Delimiter { get; init; } = ',';
    public char Quote { get; init; } = '"';

    /// <summary>"." or "," — the decimal mark used in amount fields.</summary>
    public string DecimalSeparator { get; init; } = ".";

    /// <summary>When true, amount/fee fields are already integer minor units (no decimal point).</summary>
    public bool AmountInMinorUnits { get; init; }

    public IReadOnlyList<string> DateFormats { get; init; } = new[] { "yyyy-MM-dd" };

    /// <summary>Fixed UTC offset of the source system, e.g. "+03:00" for Nairobi.</summary>
    public string SourceUtcOffset { get; init; } = "+00:00";

    public string DefaultCurrency { get; init; } = "USD";

    /// <summary>Multiplier applied to parsed amounts to normalise sign conventions (+1 or -1).</summary>
    public int AmountSign { get; init; } = 1;

    public ReferenceCanonicalizationRules Canonicalization { get; init; } = ReferenceCanonicalizationRules.Default;

    public IReadOnlyList<FieldSpec> Fields { get; init; } = Array.Empty<FieldSpec>();
}

/// <summary>A single tokenised source row: the physical start line, the split fields and the raw text.</summary>
public sealed record TokenizedRow(int LineNumber, IReadOnlyList<string> Fields, string RawLine)
{
    public bool IsBlank => string.IsNullOrWhiteSpace(RawLine);
}

/// <summary>
/// Streaming reader that turns a file stream into tokenised rows without loading the whole file into
/// memory. Implemented per format (CSV, fixed-width) in the infrastructure layer.
/// </summary>
public interface IRowTokenizer
{
    RecordFileFormat Format { get; }

    IAsyncEnumerable<TokenizedRow> TokenizeAsync(Stream stream, FileFormatProfile profile, CancellationToken ct = default);
}
