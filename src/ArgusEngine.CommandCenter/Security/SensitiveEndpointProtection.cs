using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.Security;

public static class SensitiveEndpointProtection
{
    public const string DiagnosticsPolicyName = "DiagnosticsApiKey";
    public const string MaintenancePolicyName = "MaintenanceApiKey";

    private static readonly SensitiveEndpointRateLimiter RateLimiter = new();

    public static IApplicationBuilder UseSensitiveEndpointProtection(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var endpointKind = Classify(context.Request.Path);

            if (endpointKind is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var auditLogger = loggerFactory.CreateLogger("ArgusEngine.CommandCenter.SensitiveEndpointAudit");

            var policy = SensitiveEndpointPolicy.FromConfiguration(config, endpointKind.Value);

            if (!policy.Enabled)
            {
                Audit(auditLogger, context, endpointKind.Value, "disabled", StatusCodes.Status404NotFound, null);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (string.IsNullOrWhiteSpace(policy.ApiKey))
            {
                Audit(auditLogger, context, endpointKind.Value, "misconfigured", StatusCodes.Status503ServiceUnavailable, null);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        error = "sensitive_endpoint_misconfigured",
                        detail = $"{policy.ConfigurationPrefix}:Enabled=true requires {policy.ConfigurationPrefix}:ApiKey."
                    },
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var suppliedKey = ReadApiKey(context, endpointKind.Value);
            var authenticated = IsAuthorized(suppliedKey, policy.ApiKey);

            if (!authenticated)
            {
                Audit(auditLogger, context, endpointKind.Value, "unauthorized", StatusCodes.Status401Unauthorized, null);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var partition = RateLimitPartition(context, endpointKind.Value, suppliedKey);
            if (!RateLimiter.TryAcquire(
                    partition,
                    policy.RateLimitPermitLimit,
                    TimeSpan.FromSeconds(policy.RateLimitWindowSeconds),
                    DateTimeOffset.UtcNow,
                    out var retryAfter))
            {
                context.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                Audit(auditLogger, context, endpointKind.Value, "rate_limited", StatusCodes.Status429TooManyRequests, suppliedKey);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        error = "rate_limited",
                        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    },
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            Audit(auditLogger, context, endpointKind.Value, "authorized", null, suppliedKey);

            await next(context).ConfigureAwait(false);
        });
    }

    public static SensitiveEndpointKind? Classify(PathString path)
    {
        if (path.StartsWithSegments("/api/diagnostics", StringComparison.OrdinalIgnoreCase))
            return SensitiveEndpointKind.Diagnostics;

        if (path.StartsWithSegments("/api/maintenance", StringComparison.OrdinalIgnoreCase))
            return SensitiveEndpointKind.Maintenance;

        return null;
    }

    public static bool IsAuthorized(string? suppliedKey, string? requiredKey)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey) || string.IsNullOrWhiteSpace(requiredKey))
            return false;

        var supplied = Encoding.UTF8.GetBytes(suppliedKey.Trim());
        var required = Encoding.UTF8.GetBytes(requiredKey.Trim());

        return supplied.Length == required.Length
            && CryptographicOperations.FixedTimeEquals(supplied, required);
    }

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string? ReadApiKey(HttpContext context, SensitiveEndpointKind kind)
    {
        var headers = context.Request.Headers;

        return kind switch
        {
            SensitiveEndpointKind.Diagnostics =>
                FirstNonEmpty(
                    headers["X-Argus-Diagnostics-Key"].ToString(),
                    headers["X-Nightmare-Diagnostics-Key"].ToString()),

            SensitiveEndpointKind.Maintenance =>
                FirstNonEmpty(
                    headers["X-Argus-Maintenance-Key"].ToString(),
                    headers["X-Nightmare-Maintenance-Key"].ToString()),

            _ => null
        };
    }

    private static string RateLimitPartition(HttpContext context, SensitiveEndpointKind kind, string? suppliedKey)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var ip = string.IsNullOrWhiteSpace(forwarded)
            ? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            : forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

        return $"{kind}:{ip}:{Fingerprint(suppliedKey)}";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static void Audit(
        ILogger auditLogger,
        HttpContext context,
        SensitiveEndpointKind kind,
        string outcome,
        int? statusCode,
        string? suppliedKey)
    {
        auditLogger.LogWarning(
            "Sensitive endpoint audit kind={Kind} method={Method} path={Path} outcome={Outcome} status={StatusCode} remoteIp={RemoteIp} apiKeyFingerprint={ApiKeyFingerprint} userAgent={UserAgent}",
            kind,
            context.Request.Method,
            context.Request.Path.Value,
            outcome,
            statusCode?.ToString(CultureInfo.InvariantCulture) ?? "pass",
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Fingerprint(suppliedKey),
            context.Request.Headers["User-Agent"].ToString());
    }
}

public enum SensitiveEndpointKind
{
    Diagnostics,
    Maintenance
}

public sealed record SensitiveEndpointPolicy(
    SensitiveEndpointKind Kind,
    bool Enabled,
    string? ApiKey,
    int RateLimitPermitLimit,
    int RateLimitWindowSeconds,
    string ConfigurationPrefix)
{
    public static SensitiveEndpointPolicy FromConfiguration(IConfiguration configuration, SensitiveEndpointKind kind)
    {
        var sectionName = kind == SensitiveEndpointKind.Diagnostics ? "Diagnostics" : "DataMaintenance";
        var prefix = $"Argus:{sectionName}";

        return new SensitiveEndpointPolicy(
            kind,
            GetBool(configuration, $"Argus:{sectionName}:Enabled", $"Nightmare:{sectionName}:Enabled", defaultValue: false),
            GetString(configuration, $"Argus:{sectionName}:ApiKey", $"Nightmare:{sectionName}:ApiKey"),
            GetInt(configuration, $"Argus:{sectionName}:RateLimit:PermitLimit", $"Nightmare:{sectionName}:RateLimit:PermitLimit", defaultValue: kind == SensitiveEndpointKind.Diagnostics ? 60 : 20),
            GetInt(configuration, $"Argus:{sectionName}:RateLimit:WindowSeconds", $"Nightmare:{sectionName}:RateLimit:WindowSeconds", defaultValue: 60),
            prefix);
    }

    private static string? GetString(IConfiguration configuration, string argusKey, string nightmareKey)
    {
        return configuration[argusKey] ?? configuration[nightmareKey];
    }

    private static bool GetBool(IConfiguration configuration, string argusKey, string nightmareKey, bool defaultValue)
    {
        var value = GetString(configuration, argusKey, nightmareKey);

        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var parsed) && parsed;
    }

    private static int GetInt(IConfiguration configuration, string argusKey, string nightmareKey, int defaultValue)
    {
        var value = GetString(configuration, argusKey, nightmareKey);

        if (!int.TryParse(value, out var parsed))
            return defaultValue;

        return Math.Clamp(parsed, 1, 10_000);
    }
}

public sealed class SensitiveEndpointRateLimiter
{
    private readonly ConcurrentDictionary<string, RateWindow> _windows = new();

    public bool TryAcquire(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        DateTimeOffset now,
        out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        if (permitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(permitLimit), "Permit limit must be positive.");

        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "Rate-limit window must be positive.");

        var current = _windows.AddOrUpdate(
            partitionKey,
            _ => new RateWindow(now, 1),
            (_, existing) =>
            {
                if (now - existing.StartedAtUtc >= window)
                    return new RateWindow(now, 1);

                return existing with { Count = existing.Count + 1 };
            });

        if (current.Count <= permitLimit)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = (current.StartedAtUtc + window) - now;
        if (retryAfter < TimeSpan.Zero)
            retryAfter = TimeSpan.Zero;

        return false;
    }

    private sealed record RateWindow(DateTimeOffset StartedAtUtc, int Count);
}
