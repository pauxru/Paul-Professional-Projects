namespace JobScheduler.Domain.Scheduling;

/// <summary>
/// Resolves a time-zone id tolerant of both IANA (<c>America/New_York</c>) and Windows
/// (<c>Eastern Standard Time</c>) naming, so the same job definition is portable across
/// Linux and Windows hosts. .NET 6+ converts between the two, but we fall back explicitly.
/// </summary>
public static class TimeZoneResolver
{
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) &&
                win is not null &&
                TryFind(win, out var byWin))
            {
                return byWin;
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) &&
                iana is not null &&
                TryFind(iana, out var byIana))
            {
                return byIana;
            }

            throw;
        }
    }

    public static bool IsValid(string? id)
    {
        try
        {
            _ = Resolve(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }

    private static bool TryFind(string id, out TimeZoneInfo tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
            return false;
        }
    }
}
