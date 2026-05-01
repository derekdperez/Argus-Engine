using System.Globalization;

namespace NightmareV2.CommandCenter;

internal static class AppTimeFormat
{
    private static readonly TimeSpan EasternStandardOffset = TimeSpan.FromHours(-5);

    public static string Format(DateTimeOffset? value) =>
        value is null ? "-" : Format(value.Value);

    public static string Format(DateTimeOffset value) =>
        value.ToOffset(EasternStandardOffset).ToString("MM/dd/yyyy hh:mm:ss", CultureInfo.InvariantCulture);
}
