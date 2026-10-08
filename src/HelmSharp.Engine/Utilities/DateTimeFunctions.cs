using System.Globalization;

namespace HelmSharp.Engine;

/// <summary>
/// Pure date/time formatting helpers used by template functions.
/// All arguments are pre-resolved — no TemplateContext or token dependency.
/// </summary>
internal static class DateTimeFunctions
{
    /// <summary>
    /// Sprig <c>date</c>: formats <paramref name="value"/> with a .NET format string.
    /// When the value is not a date (or is null), formats the current UTC time —
    /// matching Sprig's behavior of falling back to <c>now</c>.
    /// </summary>
    public static string Format(string format, object? value)
    {
        if (value is DateTimeOffset dto) return dto.ToString(format, CultureInfo.InvariantCulture);
        if (value is DateTime dt) return dt.ToString(format, CultureInfo.InvariantCulture);
        return DateTimeOffset.UtcNow.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sprig <c>dateInZone</c>: like <see cref="Format"/> but converts the value to
    /// the given IANA/Windows time zone first. Unknown zone IDs or conversion
    /// failures silently fall back to formatting the current UTC time.
    /// </summary>
    public static string FormatInZone(string format, string timeZoneId, object? value)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            if (value is DateTimeOffset dto) return TimeZoneInfo.ConvertTime(dto, tz).ToString(format, CultureInfo.InvariantCulture);
            if (value is DateTime dt) return TimeZoneInfo.ConvertTime(dt, tz).ToString(format, CultureInfo.InvariantCulture);
        }
        catch { }
        return DateTimeOffset.UtcNow.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sprig <c>duration</c>: renders a second count as Go's default duration format
    /// (<c>1d 2h 3m 4s</c>). Go's <c>time.Duration.String()</c> uses h/m/s units only;
    /// the day unit is included here for readability in chart output.
    /// </summary>
    public static string Duration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"d\d\ h\h\ m\m\ s\s", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sprig <c>durationRound</c>: renders a second count in its single largest
    /// unit (e.g. <c>2h</c>), truncating the remainder.
    /// </summary>
    public static string DurationRound(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }
}
