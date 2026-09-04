using System.Text;

namespace Archaeologist.Core;

public enum Verdict { Unsettled, Held, Contradicted }

/// <summary>
/// A claim written down before the measurement, and the measurement that settled it.
/// The report will not render while any prediction is unsettled, which makes it
/// impossible to quietly drop the ones that came out badly.
/// </summary>
public sealed class Prediction(string id, string statement)
{
    public string Id { get; } = id;
    public string Statement { get; } = statement;
    public string? Evidence { get; private set; }
    public Verdict Verdict { get; private set; } = Verdict.Unsettled;

    public void Held(string evidence) => Settle(Verdict.Held, evidence);
    public void Contradicted(string evidence) => Settle(Verdict.Contradicted, evidence);

    private void Settle(Verdict v, string evidence)
    {
        if (Verdict != Verdict.Unsettled)
            throw new InvalidOperationException($"{Id} settled twice");
        if (string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException($"{Id} settled without evidence", nameof(evidence));
        Verdict = v;
        Evidence = evidence;
    }

    public string Marker => Verdict switch
    {
        Verdict.Held => "HELD",
        Verdict.Contradicted => "CONTRADICTED",
        _ => "UNSETTLED",
    };
}

public sealed class Report
{
    private readonly StringBuilder _sb = new();
    private readonly List<Prediction> _predictions = [];

    public IReadOnlyList<Prediction> Predictions => _predictions;

    public Prediction Expect(string id, string statement)
    {
        if (_predictions.Any(p => p.Id == id))
            throw new InvalidOperationException($"duplicate prediction id {id}");
        var p = new Prediction(id, statement);
        _predictions.Add(p);
        _sb.Append("**").Append(id).Append(" -- expected.** ").Append(statement).Append("\n\n");
        return p;
    }

    public Report Settle(Prediction p)
    {
        if (p.Verdict == Verdict.Unsettled)
            throw new InvalidOperationException($"{p.Id} was never settled");
        _sb.Append("**").Append(p.Id).Append(" -- ").Append(p.Marker).Append(".** ")
            .Append(p.Evidence).Append("\n\n");
        return this;
    }

    public Report H1(string s) { _sb.Append("# ").Append(s).Append("\n\n"); return this; }
    public Report H2(string s) { _sb.Append("## ").Append(s).Append("\n\n"); return this; }
    public Report H3(string s) { _sb.Append("### ").Append(s).Append("\n\n"); return this; }
    public Report P(string s) { _sb.Append(s).Append("\n\n"); return this; }

    public Report Bullets(IEnumerable<string> items)
    {
        foreach (var i in items) _sb.Append("- ").Append(i).Append('\n');
        _sb.Append('\n');
        return this;
    }

    public Report Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        if (headers.Count == 0)
            throw new ArgumentException("a table needs at least one column", nameof(headers));
        _sb.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n");
        _sb.Append('|').Append(string.Join("|", headers.Select(_ => "---"))).Append("|\n");
        foreach (var r in rows)
        {
            if (r.Count != headers.Count)
                throw new ArgumentException(
                    $"row has {r.Count} cells, header has {headers.Count}", nameof(rows));
            _sb.Append("| ").Append(string.Join(" | ", r)).Append(" |\n");
        }
        _sb.Append('\n');
        return this;
    }

    public Report Code(string body)
    {
        _sb.Append("```\n").Append(body.TrimEnd('\n')).Append("\n```\n\n");
        return this;
    }

    public string Render()
    {
        var unsettled = _predictions.Where(p => p.Verdict == Verdict.Unsettled).Select(p => p.Id).ToList();
        if (unsettled.Count > 0)
            throw new InvalidOperationException(
                "unsettled predictions: " + string.Join(", ", unsettled));

        var text = _sb.ToString().Replace("\r\n", "\n");

        // The console this runs on is code page 1252. A report that renders as question
        // marks in the terminal is a report nobody reads.
        foreach (var c in text)
            if (c > 127)
                throw new InvalidOperationException(
                    $"non-ASCII character U+{(int)c:X4} in report");

        return text;
    }
}
