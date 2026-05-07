namespace ArgusEngine.Application.Http;

/// <summary>
/// Manages per-domain rate limiting for HTTP requests.
/// </summary>
public interface IHttpRateLimiter
{
    /// <summary>
    /// Waits for the rate limit to allow a request to the specified domain.
    /// </summary>
    Task WaitAsync(string domainKey, CancellationToken ct);

    /// <summary>
    /// Records the completion of a request, potentially adjusting future rate limits.
    /// </summary>
    void RecordCompletion(string domainKey, bool success, TimeSpan duration);
}
