namespace NightmareV2.Application.Assets;

/// <summary>
/// Captured HTTP exchange when a URL-class asset is confirmed.
/// Full bodies should normally live in the HTTP artifact store; keep ResponseBody
/// only as a compatibility/fallback field.
/// </summary>
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
    IReadOnlyList<UrlRedirectHop>? RedirectChain = null,
    Guid? RequestHeadersBlobId = null,
    Guid? RequestBodyBlobId = null,
    Guid? ResponseHeadersBlobId = null,
    Guid? ResponseBodyBlobId = null,
    Guid? RedirectChainBlobId = null,
    string? ResponseBodySha256 = null,
    string? ResponseBodyPreview = null,
    bool ResponseBodyTruncated = false)
{
    /// <summary>Same as ResponseSizeBytes; use when mirroring HTTP Content-Length semantics.</summary>
    public long? ContentLength => ResponseSizeBytes;
}

public sealed record UrlRedirectHop(
    string FromUrl,
    string ToUrl,
    int StatusCode,
    string? LocationHeader);
