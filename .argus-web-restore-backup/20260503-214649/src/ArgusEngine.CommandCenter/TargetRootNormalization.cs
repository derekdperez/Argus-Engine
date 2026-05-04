using System.Globalization;

namespace ArgusEngine.CommandCenter;

internal static class TargetRootNormalization
{
    public static bool TryNormalize(string input, out string root)
    {
        root = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var candidate = input.Trim();
        if (candidate.StartsWith("*.", StringComparison.Ordinal))
            candidate = candidate[2..];

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
        {
            candidate = absolute.Host;
        }
        else if (candidate.IndexOfAny(['/', ':', '?', '#']) >= 0)
        {
            if (!Uri.TryCreate("https://" + candidate, UriKind.Absolute, out var inferred)
                || string.IsNullOrWhiteSpace(inferred.Host))
            {
                return false;
            }

            candidate = inferred.Host;
        }

        candidate = candidate.Trim().TrimEnd('.').ToLowerInvariant();
        if (candidate.Length is 0 or > 253)
            return false;
        if (candidate.Contains(' ') || candidate.Contains("..", StringComparison.Ordinal))
            return false;

        try
        {
            candidate = new IdnMapping().GetAscii(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var labels = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
            return false;

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                return false;
            if (label[0] == '-' || label[^1] == '-')
                return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return false;
        }

        root = candidate;
        return true;
    }

    public static IEnumerable<string> SplitLines(string text) =>
        text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
}
