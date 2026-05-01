namespace NightmareV2.Application.Assets;

/// <summary>Captured HTTP exchange when a URL-class asset is confirmed.</summary>
public sealed record UrlFetchSnapshot(
    string RequestMethod,
    Dictionary<string, string> RequestHeaders,
    string? RequestBody,
    int StatusCode,
    Dictionary<string, string> ResponseHeaders,
    string? ResponseBody,
    long? ResponseSizeBytes,
    double DurationMs,
    string? ContentType,
    DateTimeOffset CompletedAtUtc,
    string? FinalUrl = null,
    int RedirectCount = 0,
    IReadOnlyList<UrlRedirectHop>? RedirectChain = null)
{
    /// <summary>Same as <see cref="ResponseSizeBytes"/>; use when mirroring HTTP Content-Length semantics.</summary>
    public long? ContentLength => ResponseSizeBytes;
}

public sealed record UrlRedirectHop(
    string FromUrl,
    string ToUrl,
    int StatusCode,
    string? LocationHeader);

