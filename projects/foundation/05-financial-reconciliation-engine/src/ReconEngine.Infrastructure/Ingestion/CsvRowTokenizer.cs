using System.Runtime.CompilerServices;
using System.Text;
using ReconEngine.Application.Ingestion;

namespace ReconEngine.Infrastructure.Ingestion;

/// <summary>
/// A streaming, RFC-4180-style CSV reader implemented as a character state machine. It never loads the
/// whole file into memory (it reads fixed-size buffers and yields one row at a time), and it correctly
/// handles quoted delimiters, escaped quotes (<c>""</c>), embedded newlines inside quoted fields, a
/// leading UTF-8 BOM, CRLF/LF/CR line endings and blank lines (which are skipped). Each yielded row
/// carries the physical start line so row-level rejections can be reported with a line number.
/// </summary>
public sealed class CsvRowTokenizer : IRowTokenizer
{
    private const int BufferSize = 16 * 1024;

    public RecordFileFormat Format => RecordFileFormat.Csv;

    private enum State { FieldStart, Unquoted, Quoted, AfterQuote }

    public async IAsyncEnumerable<TokenizedRow> TokenizeAsync(
        Stream stream, FileFormatProfile profile, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var delimiter = profile.Delimiter;
        var quote = profile.Quote;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: true);

        var buffer = new char[BufferSize];
        var state = State.FieldStart;
        var field = new StringBuilder();
        var raw = new StringBuilder();
        var fields = new List<string>();
        var recordDirty = false;
        var skipLf = false;
        var currentLine = 1;
        var recordStartLine = 1;
        var isFirstChar = true;

        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                // Strip a stray BOM if the StreamReader did not (defensive).
                if (isFirstChar)
                {
                    isFirstChar = false;
                    if (c == '\uFEFF')
                        continue;
                }

                if (skipLf)
                {
                    skipLf = false;
                    if (c == '\n')
                    {
                        // Second half of a CRLF already handled on the CR.
                        if (state == State.Quoted)
                            field.Append('\n');
                        continue;
                    }
                }

                switch (state)
                {
                    case State.Quoted:
                        if (c == quote)
                        {
                            state = State.AfterQuote;
                        }
                        else if (c is '\r' or '\n')
                        {
                            field.Append('\n');
                            recordDirty = true;
                            currentLine++;
                            if (c == '\r') skipLf = true;
                        }
                        else
                        {
                            field.Append(c);
                            recordDirty = true;
                        }
                        break;

                    case State.AfterQuote:
                        if (c == quote)
                        {
                            field.Append(quote);
                            recordDirty = true;
                            state = State.Quoted;
                        }
                        else if (c == delimiter)
                        {
                            CloseField(fields, field, raw, delimiter);
                            state = State.FieldStart;
                        }
                        else if (c is '\r' or '\n')
                        {
                            if (TryYield(fields, field, ref recordDirty, recordStartLine, raw, out var row))
                            {
                                yield return row;
                            }
                            currentLine++;
                            if (c == '\r') skipLf = true;
                            recordStartLine = currentLine;
                            state = State.FieldStart;
                        }
                        else
                        {
                            field.Append(c);
                            recordDirty = true;
                            state = State.Unquoted;
                        }
                        break;

                    default: // FieldStart or Unquoted
                        if (c == quote && state == State.FieldStart)
                        {
                            state = State.Quoted;
                            recordDirty = true;
                        }
                        else if (c == delimiter)
                        {
                            CloseField(fields, field, raw, delimiter);
                            state = State.FieldStart;
                        }
                        else if (c is '\r' or '\n')
                        {
                            if (TryYield(fields, field, ref recordDirty, recordStartLine, raw, out var row))
                            {
                                yield return row;
                            }
                            currentLine++;
                            if (c == '\r') skipLf = true;
                            recordStartLine = currentLine;
                            state = State.FieldStart;
                        }
                        else
                        {
                            field.Append(c);
                            raw.Append(c);
                            recordDirty = true;
                            state = State.Unquoted;
                        }
                        break;
                }
            }
        }

        // Flush any trailing record that was not terminated by a newline.
        if (TryYield(fields, field, ref recordDirty, recordStartLine, raw, out var last))
            yield return last;
    }

    private static void CloseField(List<string> fields, StringBuilder field, StringBuilder raw, char delimiter)
    {
        fields.Add(field.ToString());
        field.Clear();
        raw.Append(delimiter);
    }

    private static bool TryYield(
        List<string> fields, StringBuilder field, ref bool recordDirty, int recordStartLine, StringBuilder raw, out TokenizedRow row)
    {
        var shouldYield = recordDirty || fields.Count > 0;
        if (!shouldYield)
        {
            // Blank line: reset and skip.
            field.Clear();
            fields.Clear();
            raw.Clear();
            row = default!;
            return false;
        }

        fields.Add(field.ToString());
        row = new TokenizedRow(recordStartLine, fields.ToArray(), raw.ToString());

        field.Clear();
        fields.Clear();
        raw.Clear();
        recordDirty = false;
        return true;
    }
}
