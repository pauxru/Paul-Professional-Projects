using System.Globalization;
using System.Text;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;

namespace LoadRunner.Reporting.Svg;

/// <summary>
/// Hand-rolled minimal SVG chart generator. The charts are pure text (no binary images)
/// so the reports work through any static file server or GitHub renderer. They intentionally
/// aim for legibility over flair — good axes, tidy labels, no legend clutter unless needed.
/// </summary>
public static class SvgCharts
{
    private const int Width = 800;
    private const int Height = 320;
    private const int MarginLeft = 60;
    private const int MarginBottom = 40;
    private const int MarginTop = 20;
    private const int MarginRight = 20;

    public static string LatencyOverTime(IReadOnlyList<TimeSeriesPoint> series, string title = "Latency over time (ms)")
        => Line(series
                .Select(p => (x: (p.TimestampUtc - series[0].TimestampUtc).TotalSeconds, y: p.P95Ms))
                .ToArray(),
            title, "seconds since start", "p95 ms", "#2b6cb0");

    public static string ThroughputOverTime(IReadOnlyList<TimeSeriesPoint> series, string title = "Requests per second")
        => Line(series
                .Select(p => (x: (p.TimestampUtc - series[0].TimestampUtc).TotalSeconds, y: (double)p.Count))
                .ToArray(),
            title, "seconds since start", "rps", "#38a169");

    public static string ErrorsOverTime(IReadOnlyList<TimeSeriesPoint> series, string title = "Errors per second")
        => Line(series
                .Select(p => (x: (p.TimestampUtc - series[0].TimestampUtc).TotalSeconds, y: (double)p.Errors))
                .ToArray(),
            title, "seconds since start", "errors", "#c53030");

    public static string PercentileDistribution(LatencyStats stats, string title = "Percentile latency (ms)")
    {
        var data = new (double x, double y)[]
        {
            (50, stats.P50Ms),
            (75, stats.P75Ms),
            (90, stats.P90Ms),
            (95, stats.P95Ms),
            (99, stats.P99Ms),
            (99.9, stats.P999Ms),
        };
        return Bars(data, title, "percentile", "latency ms", "#805ad5");
    }

    public static string ComparisonOverlay(
        IReadOnlyList<TimeSeriesPoint> a,
        IReadOnlyList<TimeSeriesPoint> b,
        string title = "p95 latency comparison (ms)")
    {
        var aPts = a
            .Select(p => (x: (p.TimestampUtc - a[0].TimestampUtc).TotalSeconds, y: p.P95Ms))
            .ToArray();
        var bPts = b
            .Select(p => (x: (p.TimestampUtc - b[0].TimestampUtc).TotalSeconds, y: p.P95Ms))
            .ToArray();
        return DualLine(aPts, bPts, title, "seconds since start", "p95 ms", "#c53030", "#2b6cb0", "baseline", "candidate");
    }

    private static string Line((double x, double y)[] points, string title, string xLabel, string yLabel, string color)
    {
        if (points.Length == 0) return EmptyChart(title);
        var (xMin, xMax, yMin, yMax) = Bounds(points);
        var plotW = Width - MarginLeft - MarginRight;
        var plotH = Height - MarginTop - MarginBottom;
        double Sx(double x) => MarginLeft + (xMax == xMin ? 0 : (x - xMin) / (xMax - xMin) * plotW);
        double Sy(double y) => MarginTop + plotH - (yMax == yMin ? 0 : (y - yMin) / (yMax - yMin) * plotH);

        var sb = new StringBuilder();
        Header(sb, title);
        Axes(sb, xMin, xMax, yMin, yMax, xLabel, yLabel);
        sb.Append("<polyline fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2\" points=\"");
        foreach (var (x, y) in points)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", Sx(x), Sy(y));
        sb.Append("\" />");
        Footer(sb);
        return sb.ToString();
    }

    private static string DualLine(
        (double x, double y)[] a, (double x, double y)[] b,
        string title, string xLabel, string yLabel,
        string colorA, string colorB, string labelA, string labelB)
    {
        if (a.Length == 0 && b.Length == 0) return EmptyChart(title);
        var xMin = 0.0;
        var xMax = Math.Max(a.Length > 0 ? a[^1].x : 0, b.Length > 0 ? b[^1].x : 0);
        var yMin = 0.0;
        var yMax = Math.Max(
            a.Length > 0 ? a.Max(p => p.y) : 0,
            b.Length > 0 ? b.Max(p => p.y) : 0);
        var plotW = Width - MarginLeft - MarginRight;
        var plotH = Height - MarginTop - MarginBottom;
        double Sx(double x) => MarginLeft + (xMax == xMin ? 0 : (x - xMin) / (xMax - xMin) * plotW);
        double Sy(double y) => MarginTop + plotH - (yMax == yMin ? 0 : (y - yMin) / (yMax - yMin) * plotH);

        var sb = new StringBuilder();
        Header(sb, title);
        Axes(sb, xMin, xMax, yMin, yMax, xLabel, yLabel);
        WriteLine(sb, a, Sx, Sy, colorA);
        WriteLine(sb, b, Sx, Sy, colorB);
        // legend
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<rect x=\"{0}\" y=\"{1}\" width=\"12\" height=\"12\" fill=\"{2}\" />" +
            "<text x=\"{3}\" y=\"{4}\" font-size=\"11\">{5}</text>",
            Width - 180, MarginTop + 8, colorA, Width - 162, MarginTop + 18, System.Net.WebUtility.HtmlEncode(labelA));
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<rect x=\"{0}\" y=\"{1}\" width=\"12\" height=\"12\" fill=\"{2}\" />" +
            "<text x=\"{3}\" y=\"{4}\" font-size=\"11\">{5}</text>",
            Width - 90, MarginTop + 8, colorB, Width - 72, MarginTop + 18, System.Net.WebUtility.HtmlEncode(labelB));
        Footer(sb);
        return sb.ToString();
    }

    private static void WriteLine(StringBuilder sb, (double x, double y)[] pts, Func<double, double> sx, Func<double, double> sy, string color)
    {
        if (pts.Length == 0) return;
        sb.Append("<polyline fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2\" points=\"");
        foreach (var (x, y) in pts)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", sx(x), sy(y));
        sb.Append("\" />");
    }

    private static string Bars((double x, double y)[] points, string title, string xLabel, string yLabel, string color)
    {
        if (points.Length == 0) return EmptyChart(title);
        var xMin = 0.0;
        var xMax = points.Length;
        var yMin = 0.0;
        var yMax = points.Max(p => p.y) * 1.15;
        if (yMax == 0) yMax = 1;
        var plotW = Width - MarginLeft - MarginRight;
        var plotH = Height - MarginTop - MarginBottom;
        var barW = plotW / (double)points.Length * 0.7;
        var gap = plotW / (double)points.Length * 0.3;

        var sb = new StringBuilder();
        Header(sb, title);
        // draw axes
        AxisLine(sb, xLabel, yLabel, xMin, xMax, yMin, yMax);
        for (var i = 0; i < points.Length; i++)
        {
            var x0 = MarginLeft + (i + 0.5) * (plotW / points.Length) - barW / 2;
            var h = (points[i].y / yMax) * plotH;
            var y0 = MarginTop + plotH - h;
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<rect x=\"{0:F1}\" y=\"{1:F1}\" width=\"{2:F1}\" height=\"{3:F1}\" fill=\"{4}\" />",
                x0, y0, barW, h, color);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<text x=\"{0:F1}\" y=\"{1}\" font-size=\"11\" text-anchor=\"middle\">p{2}</text>",
                x0 + barW / 2, Height - MarginBottom + 15, points[i].x);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<text x=\"{0:F1}\" y=\"{1:F1}\" font-size=\"10\" text-anchor=\"middle\">{2:F1}</text>",
                x0 + barW / 2, y0 - 4, points[i].y);
        }
        Footer(sb);
        return sb.ToString();
    }

    private static (double, double, double, double) Bounds((double x, double y)[] points)
    {
        var xMin = points.Min(p => p.x);
        var xMax = points.Max(p => p.x);
        var yMin = 0.0;
        var yMax = Math.Max(points.Max(p => p.y) * 1.1, 1);
        if (xMax == xMin) xMax = xMin + 1;
        return (xMin, xMax, yMin, yMax);
    }

    private static void Header(StringBuilder sb, string title)
    {
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(Width).Append(' ').Append(Height).Append("\" style=\"font-family: system-ui, sans-serif; background:#fff;\">");
        sb.Append("<text x=\"").Append(MarginLeft).Append("\" y=\"14\" font-size=\"13\" font-weight=\"600\">")
            .Append(System.Net.WebUtility.HtmlEncode(title))
            .Append("</text>");
    }

    private static void Footer(StringBuilder sb) => sb.Append("</svg>");

    private static void Axes(StringBuilder sb, double xMin, double xMax, double yMin, double yMax, string xLabel, string yLabel)
    {
        AxisLine(sb, xLabel, yLabel, xMin, xMax, yMin, yMax);
    }

    private static void AxisLine(StringBuilder sb, string xLabel, string yLabel, double xMin, double xMax, double yMin, double yMax)
    {
        var plotBottom = Height - MarginBottom;
        var plotRight = Width - MarginRight;
        // axes
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<line x1=\"{0}\" y1=\"{1}\" x2=\"{0}\" y2=\"{2}\" stroke=\"#4a5568\" stroke-width=\"1\" />",
            MarginLeft, MarginTop, plotBottom);
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{1}\" stroke=\"#4a5568\" stroke-width=\"1\" />",
            MarginLeft, plotBottom, plotRight);
        // labels
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<text x=\"{0}\" y=\"{1}\" font-size=\"10\" text-anchor=\"middle\">{2}</text>",
            (MarginLeft + plotRight) / 2, plotBottom + 30, System.Net.WebUtility.HtmlEncode(xLabel));
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<text x=\"{0}\" y=\"{1}\" font-size=\"10\" transform=\"rotate(-90 {0} {1})\" text-anchor=\"middle\">{2}</text>",
            15, (MarginTop + plotBottom) / 2, System.Net.WebUtility.HtmlEncode(yLabel));
        // y ticks (0, 25, 50, 75, 100 %)
        for (var i = 0; i <= 4; i++)
        {
            var v = yMin + (yMax - yMin) * i / 4.0;
            var y = MarginTop + (Height - MarginTop - MarginBottom) * (1 - i / 4.0);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<line x1=\"{0}\" y1=\"{1:F1}\" x2=\"{2}\" y2=\"{1:F1}\" stroke=\"#e2e8f0\" stroke-width=\"1\" />",
                MarginLeft, y, plotRight);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<text x=\"{0}\" y=\"{1:F1}\" font-size=\"10\" text-anchor=\"end\">{2:F1}</text>",
                MarginLeft - 5, y + 3, v);
        }
    }

    private static string EmptyChart(string title)
    {
        var sb = new StringBuilder();
        Header(sb, title);
        sb.Append("<text x=\"").Append(Width / 2).Append("\" y=\"").Append(Height / 2)
            .Append("\" text-anchor=\"middle\" font-size=\"12\" fill=\"#a0aec0\">no data</text>");
        Footer(sb);
        return sb.ToString();
    }
}
