using System.Text;

namespace ArgusEngine.Workers.Spider;

internal static class BoundedHttpContentReader
{
    internal static async Task<string> ReadAsStringAsync(HttpContent content, int maxChars, CancellationToken cancellationToken)
    {
        if (maxChars <= 0)
            return string.Empty;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            ResolveEncoding(content.Headers.ContentType?.CharSet),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: false);

        var buffer = new char[Math.Min(8192, maxChars)];
        var sb = new StringBuilder(capacity: Math.Min(maxChars, 8192));
        while (sb.Length < maxChars)
        {
            var remaining = maxChars - sb.Length;
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
