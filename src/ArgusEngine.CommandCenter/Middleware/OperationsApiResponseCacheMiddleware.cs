using Microsoft.Extensions.Caching.Memory;

namespace ArgusEngine.CommandCenter.Middleware;

public sealed class OperationsApiResponseCacheMiddleware
{
    private const long MaxCachedResponseBytes = 2 * 1024 * 1024;
    private readonly RequestDelegate _next;

    public OperationsApiResponseCacheMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMemoryCache cache,
        ILogger<OperationsApiResponseCacheMiddleware> logger)
    {
        if (!CanCache(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var ttl = GetTimeToLive(context.Request.Path);
        var cacheKey = $"argus-live-api:{context.Request.Path.Value?.ToLowerInvariant()}{context.Request.QueryString.Value}";

        if (cache.TryGetValue<CachedApiResponse>(cacheKey, out var cached) && cached is not null)
        {
            context.Response.StatusCode = cached.StatusCode;
            context.Response.ContentType = cached.ContentType;
            context.Response.Headers["X-Argus-Cache"] = "HIT";
            context.Response.Headers.CacheControl = $"private, max-age={(int)Math.Ceiling(ttl.TotalSeconds)}";
            await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capture = new MemoryStream();
        context.Response.Body = capture;

        try
        {
            await _next(context).ConfigureAwait(false);

            var shouldCache =
                context.Response.StatusCode == StatusCodes.Status200OK
                && capture.Length > 0
                && capture.Length <= MaxCachedResponseBytes
                && IsJsonResponse(context.Response.ContentType);

            if (shouldCache)
            {
                var body = capture.ToArray();
                cache.Set(
                    cacheKey,
                    new CachedApiResponse(context.Response.StatusCode, context.Response.ContentType, body),
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = ttl,
                        Size = body.Length,
                    });

                context.Response.Headers["X-Argus-Cache"] = "MISS";
                context.Response.Headers.CacheControl = $"private, max-age={(int)Math.Ceiling(ttl.TotalSeconds)}";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Live API cache passthrough failed for {Path}.", context.Request.Path);
            throw;
        }
        finally
        {
            capture.Position = 0;
            context.Response.Body = originalBody;
            await capture.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool CanCache(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return path.StartsWith("/api/ops", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/workers", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/targets", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/assets", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/http", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/bus", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/event", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetTimeToLive(PathString path)
    {
        var value = path.Value ?? string.Empty;

        if (value.Contains("rabbit", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ecs", StringComparison.OrdinalIgnoreCase)
            || value.Contains("docker", StringComparison.OrdinalIgnoreCase)
            || value.Contains("scale", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (value.StartsWith("/api/targets", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/api/assets", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(10);
        }

        return TimeSpan.FromSeconds(2);
    }

    private static bool IsJsonResponse(string? contentType)
        => contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record CachedApiResponse(int StatusCode, string? ContentType, byte[] Body);
}
