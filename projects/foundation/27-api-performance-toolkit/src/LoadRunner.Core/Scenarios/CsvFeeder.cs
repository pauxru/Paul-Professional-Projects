namespace LoadRunner.Core.Scenarios;

/// <summary>
/// CSV feeder that reads rows from a file once, then serves them either cyclically or
/// once-through. Thread-safe via a monotonic counter and modulo indexing when cycling.
/// </summary>
public sealed class CsvFeeder
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _rows;
    private long _cursor = -1;
    public bool Cycle { get; }
    public int Count => _rows.Count;

    public CsvFeeder(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, bool cycle = true)
    {
        _rows = rows;
        Cycle = cycle;
    }

    public IReadOnlyDictionary<string, string>? Next()
    {
        if (_rows.Count == 0) return null;
        var next = System.Threading.Interlocked.Increment(ref _cursor);
        if (Cycle) return _rows[(int)(next % _rows.Count)];
        if (next >= _rows.Count) return null;
        return _rows[(int)next];
    }

    public static CsvFeeder FromFile(string path, bool cycle = true)
    {
        var lines = File.ReadAllLines(path);
        return FromLines(lines, cycle);
    }

    public static CsvFeeder FromLines(IReadOnlyList<string> lines, bool cycle = true)
    {
        if (lines.Count == 0) return new CsvFeeder(Array.Empty<IReadOnlyDictionary<string, string>>(), cycle);
        var headers = SplitCsv(lines[0]);
        var rows = new List<IReadOnlyDictionary<string, string>>(lines.Count - 1);
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = SplitCsv(lines[i]);
            var dict = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count && c < cells.Count; c++)
                dict[headers[c]] = cells[c];
            rows.Add(dict);
        }
        return new CsvFeeder(rows, cycle);
    }

    /// <summary>Minimal quote-aware CSV cell splitter — good enough for feeder inputs.</summary>
    public static IReadOnlyList<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '"') inQuotes = true;
                else sb.Append(ch);
            }
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
