using System.Runtime.CompilerServices;
using System.Text;
using ReconEngine.Application.Ingestion;

namespace ReconEngine.Infrastructure.Ingestion;

/// <summary>
/// A streaming fixed-width reader. Each physical line is one record; the field boundaries are applied
/// later by the mapper using each field's <c>Start</c>/<c>Length</c> against the raw line, so this
/// tokenizer only needs to hand back the line and its number. Reads line-by-line, so a very large file
/// is never fully materialised. A leading BOM and blank lines are handled gracefully.
/// </summary>
public sealed class FixedWidthRowTokenizer : IRowTokenizer
{
    public RecordFileFormat Format => RecordFileFormat.FixedWidth;

    public async IAsyncEnumerable<TokenizedRow> TokenizeAsync(
        Stream stream, FileFormatProfile profile, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lineNumber++;

            if (lineNumber == 1 && line.Length > 0 && line[0] == '\uFEFF')
                line = line[1..];

            // A fixed-width row is a single raw line; the mapper slices columns by start/length.
            yield return new TokenizedRow(lineNumber, Array.Empty<string>(), line);
        }
    }
}
