using System.Globalization;

namespace JobScheduler.Domain.Scheduling;

/// <summary>Raised when a cron expression cannot be parsed.</summary>
public sealed class CronFormatException(string expression, string reason)
    : FormatException($"Invalid cron expression '{expression}': {reason}")
{
    public string Expression { get; } = expression;
}

/// <summary>
/// A hand-written cron parser supporting 5-field (min hour dom month dow) and 6-field
/// (sec min hour dom month dow) expressions with ranges (<c>1-5</c>), steps (<c>*/5</c>,
/// <c>0/15</c>), lists (<c>1,2,3</c>), month/day names, the Vixie day-of-month/day-of-week
/// OR rule, and the optional <c>L</c> (last) and <c>#</c> (nth) day-of-week/last-day-of-month
/// tokens. Occurrence calculation is timezone-aware and handles DST gaps (skipped) and
/// ambiguous fall-back times (fired once, on the earlier instant).
/// </summary>
public sealed class CronExpression
{
    private static readonly string[] MonthNames =
        ["", "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _seconds = new bool[60];
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32]; // 1..31
    private readonly bool[] _months = new bool[13];      // 1..12
    private readonly bool[] _daysOfWeek = new bool[7];   // 0=Sun..6=Sat
    private bool _domLast;
    private readonly List<(int Dow, int Nth)> _nthDow = [];
    private readonly List<int> _lastDow = [];
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    public string Expression { get; }

    private CronExpression(string expression, string[] fields)
    {
        Expression = expression;

        int offset = fields.Length == 6 ? 1 : 0;
        if (offset == 1)
        {
            ParseInto(fields[0], _seconds, 0, 59, null, expression);
        }
        else
        {
            _seconds[0] = true; // 5-field expressions fire at second 0
        }

        ParseInto(fields[offset + 0], _minutes, 0, 59, null, expression);
        ParseInto(fields[offset + 1], _hours, 0, 23, null, expression);
        ParseDayOfMonth(fields[offset + 2], expression);
        ParseInto(fields[offset + 3], _months, 1, 12, MonthNames, expression);
        ParseDayOfWeek(fields[offset + 4], expression);

        string dom = fields[offset + 2].Trim();
        string dow = fields[offset + 4].Trim();
        _domRestricted = dom is not ("*" or "?");
        _dowRestricted = dow is not ("*" or "?");
    }

    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new CronFormatException(expression ?? "<null>", "expression is empty");
        }

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (5 or 6))
        {
            throw new CronFormatException(expression, $"expected 5 or 6 fields but found {fields.Length}");
        }

        return new CronExpression(expression, fields);
    }

    public static bool TryParse(string expression, out CronExpression? cron)
    {
        try
        {
            cron = Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            cron = null;
            return false;
        }
    }

    // ---- parsing -------------------------------------------------------------------------

    private static void ParseInto(string field, bool[] set, int min, int max, string[]? names, string expr)
    {
        field = field.Trim();
        if (field.Length == 0)
        {
            throw new CronFormatException(expr, "empty field");
        }

        foreach (var part in field.Split(','))
        {
            ParsePart(part.Trim(), set, min, max, names, expr);
        }
    }

    private static void ParsePart(string part, bool[] set, int min, int max, string[]? names, string expr)
    {
        if (part.Length == 0)
        {
            throw new CronFormatException(expr, "empty list element");
        }

        int step = 1;
        string range = part;
        int slash = part.IndexOf('/');
        if (slash >= 0)
        {
            range = part[..slash];
            var stepText = part[(slash + 1)..];
            if (!int.TryParse(stepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step < 1)
            {
                throw new CronFormatException(expr, $"invalid step '{stepText}'");
            }
        }

        int lo, hi;
        if (range is "*" or "?")
        {
            lo = min;
            hi = max;
        }
        else if (range.Contains('-'))
        {
            var bounds = range.Split('-');
            if (bounds.Length != 2)
            {
                throw new CronFormatException(expr, $"invalid range '{range}'");
            }
            lo = ParseValue(bounds[0], names, min, max, expr);
            hi = ParseValue(bounds[1], names, min, max, expr);
        }
        else
        {
            lo = ParseValue(range, names, min, max, expr);
            hi = slash >= 0 ? max : lo; // "a/step" means a..max step
        }

        if (lo < min || hi > max || lo > hi)
        {
            throw new CronFormatException(expr, $"value(s) out of range in '{part}' (allowed {min}-{max})");
        }

        for (int v = lo; v <= hi; v += step)
        {
            set[v] = true;
        }
    }

    private static int ParseValue(string token, string[]? names, int min, int max, string expr)
    {
        token = token.Trim();
        if (names is not null)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]) &&
                    string.Equals(names[i], token, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new CronFormatException(expr, $"'{token}' is not a valid value");
        }

        if (value < min || value > max)
        {
            throw new CronFormatException(expr, $"value {value} is out of range (allowed {min}-{max})");
        }

        return value;
    }

    private void ParseDayOfMonth(string field, string expr)
    {
        field = field.Trim();
        if (field is "*" or "?")
        {
            for (int d = 1; d <= 31; d++)
            {
                _daysOfMonth[d] = true;
            }
            return;
        }

        foreach (var raw in field.Split(','))
        {
            var part = raw.Trim();
            if (string.Equals(part, "L", StringComparison.OrdinalIgnoreCase))
            {
                _domLast = true;
            }
            else
            {
                ParsePart(part, _daysOfMonth, 1, 31, null, expr);
            }
        }
    }

    private void ParseDayOfWeek(string field, string expr)
    {
        field = field.Trim();
        if (field is "*" or "?")
        {
            for (int d = 0; d < 7; d++)
            {
                _daysOfWeek[d] = true;
            }
            return;
        }

        foreach (var raw in field.Split(','))
        {
            var part = raw.Trim();
            int hash = part.IndexOf('#');
            if (hash >= 0)
            {
                int dow = Normalize(ParseValue(part[..hash], DayNames, 0, 7, expr));
                if (!int.TryParse(part[(hash + 1)..], out int nth) || nth is < 1 or > 5)
                {
                    throw new CronFormatException(expr, $"invalid nth day-of-week '{part}' (use 1-5)");
                }
                _nthDow.Add((dow, nth));
            }
            else if (part.EndsWith('L') || part.EndsWith('l'))
            {
                var head = part[..^1];
                if (head.Length == 0)
                {
                    throw new CronFormatException(expr, "bare 'L' is not valid in the day-of-week field; use e.g. 5L");
                }
                _lastDow.Add(Normalize(ParseValue(head, DayNames, 0, 7, expr)));
            }
            else
            {
                // Support 7 as Sunday by normalizing after range expansion.
                var tmp = new bool[8];
                ParsePart(part, tmp, 0, 7, DayNames, expr);
                for (int v = 0; v <= 7; v++)
                {
                    if (tmp[v])
                    {
                        _daysOfWeek[Normalize(v)] = true;
                    }
                }
            }
        }
    }

    private static int Normalize(int dow) => dow == 7 ? 0 : dow;

    // ---- matching ------------------------------------------------------------------------

    internal bool Matches(DateTime local) =>
        _seconds[local.Second] &&
        _minutes[local.Minute] &&
        _hours[local.Hour] &&
        _months[local.Month] &&
        DayMatches(local);

    private bool DayMatches(DateTime t)
    {
        bool domOk = DomMatches(t);
        bool dowOk = DowMatches(t);
        return _domRestricted && _dowRestricted ? domOk || dowOk : domOk && dowOk;
    }

    private bool DomMatches(DateTime t)
    {
        if (_daysOfMonth[t.Day])
        {
            return true;
        }
        return _domLast && t.Day == DateTime.DaysInMonth(t.Year, t.Month);
    }

    private bool DowMatches(DateTime t)
    {
        int dow = (int)t.DayOfWeek;
        if (_daysOfWeek[dow])
        {
            return true;
        }

        foreach (var (d, nth) in _nthDow)
        {
            if (d == dow && (t.Day - 1) / 7 + 1 == nth)
            {
                return true;
            }
        }

        foreach (var d in _lastDow)
        {
            if (d == dow && t.Day + 7 > DateTime.DaysInMonth(t.Year, t.Month))
            {
                return true;
            }
        }

        return false;
    }

    // ---- occurrence calculation ----------------------------------------------------------

    /// <summary>
    /// The next instant strictly after <paramref name="after"/> (or at/after it when
    /// <paramref name="inclusive"/>) at which this expression fires, evaluated in
    /// <paramref name="tz"/>. Returns null if none within a 5-year search horizon.
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after, TimeZoneInfo tz, bool inclusive = false)
    {
        ArgumentNullException.ThrowIfNull(tz);

        var localDto = TimeZoneInfo.ConvertTime(after, tz);
        var t = DateTime.SpecifyKind(
            new DateTime(localDto.Year, localDto.Month, localDto.Day, localDto.Hour, localDto.Minute, localDto.Second),
            DateTimeKind.Unspecified);
        if (!inclusive)
        {
            t = t.AddSeconds(1);
        }

        var limit = t.AddYears(5);
        while (t < limit)
        {
            if (!_months[t.Month]) { t = FirstOfNextMonth(t); continue; }
            if (!DayMatches(t)) { t = t.Date.AddDays(1); continue; }
            if (!_hours[t.Hour]) { t = Floor(t, unitHour: true).AddHours(1); continue; }
            if (!_minutes[t.Minute]) { t = Floor(t, unitHour: false).AddMinutes(1); continue; }
            if (!_seconds[t.Second]) { t = t.AddSeconds(1); continue; }

            var resolved = Resolve(t, tz);
            if (resolved is null)
            {
                t = t.AddSeconds(1); // DST gap: this wall-clock time does not exist -> skip
                continue;
            }

            var instant = resolved.Value;
            if (inclusive ? instant >= after : instant > after)
            {
                return instant;
            }

            t = t.AddSeconds(1);
        }

        return null;
    }

    /// <summary>Enumerates the next <paramref name="count"/> occurrences after <paramref name="after"/>.</summary>
    public IReadOnlyList<DateTimeOffset> GetNextOccurrences(DateTimeOffset after, int count, TimeZoneInfo tz)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var result = new List<DateTimeOffset>(count);
        var cursor = after;
        for (int i = 0; i < count; i++)
        {
            var next = GetNextOccurrence(cursor, tz);
            if (next is null)
            {
                break;
            }
            result.Add(next.Value);
            cursor = next.Value;
        }

        return result;
    }

    private static DateTime FirstOfNextMonth(DateTime t) =>
        new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);

    private static DateTime Floor(DateTime t, bool unitHour) =>
        unitHour
            ? new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Unspecified)
            : new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);

    private static DateTimeOffset? Resolve(DateTime local, TimeZoneInfo tz)
    {
        if (tz.IsInvalidTime(local))
        {
            return null;
        }

        if (tz.IsAmbiguousTime(local))
        {
            // Fall-back: the wall time occurs twice. Fire once, on the earlier instant,
            // which corresponds to the LARGER UTC offset.
            var offsets = tz.GetAmbiguousTimeOffsets(local);
            var chosen = offsets[0];
            foreach (var o in offsets)
            {
                if (o > chosen)
                {
                    chosen = o;
                }
            }
            return new DateTimeOffset(local, chosen);
        }

        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    public override string ToString() => Expression;
}
