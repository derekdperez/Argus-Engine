using System.Globalization;

namespace ArgusEngine.CommandCenter;

internal static class AppTimeFormat
{
    private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();

    public static string Format(DateTimeOffset? value)
    {
        return value is null ? "—" : Format(value.Value);
    }

    public static string Format(DateTimeOffset value)
    {
        var eastern = TimeZoneInfo.ConvertTime(value, EasternTimeZone);
        return eastern.ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture);
    }

    public static string FormatDate(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "—";
        }

        var eastern = TimeZoneInfo.ConvertTime(value.Value, EasternTimeZone);
        return eastern.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
    }

    public static string FormatTime(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "—";
        }

        var eastern = TimeZoneInfo.ConvertTime(value.Value, EasternTimeZone);
        return eastern.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture);
    }

    public static string FormatCompact(DateTimeOffset? value)
    {
        return Format(value);
    }

    public static string FormatDuration(TimeSpan? value)
    {
        if (value is null)
        {
            return "—";
        }

        return FormatDuration(value.Value);
    }

    public static string FormatDuration(TimeSpan value)
    {
        if (value.TotalSeconds < 1)
        {
            return "<1s";
        }

        if (value.TotalMinutes < 1)
        {
            return $"{Math.Round(value.TotalSeconds)}s";
        }

        if (value.TotalHours < 1)
        {
            return $"{Math.Round(value.TotalMinutes)}m";
        }

        if (value.TotalDays < 1)
        {
            return $"{Math.Round(value.TotalHours, 1)}h";
        }

        return $"{Math.Round(value.TotalDays, 1)}d";
    }

    public static string FormatDuration(long? seconds)
    {
        return seconds is null ? "—" : FormatDuration(TimeSpan.FromSeconds(Math.Max(0, seconds.Value)));
    }

    private static TimeZoneInfo ResolveEasternTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
