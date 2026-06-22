using System.Globalization;

namespace HelmSharp.Engine;

/// <summary>
/// Pure date/time formatting helpers used by template functions.
/// All arguments are pre-resolved — no TemplateContext or token dependency.
/// </summary>
internal static class DateTimeFunctions
{
    public static string Format(string format, object? value)
    {
        if (value is DateTimeOffset dto) return dto.ToString(format, CultureInfo.InvariantCulture);
        if (value is DateTime dt) return dt.ToString(format, CultureInfo.InvariantCulture);
        return DateTimeOffset.UtcNow.ToString(format, CultureInfo.InvariantCulture);
    }

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

    public static string Duration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"d\d\ h\h\ m\m\ s\s", CultureInfo.InvariantCulture);
    }

    public static string DurationRound(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }
}
