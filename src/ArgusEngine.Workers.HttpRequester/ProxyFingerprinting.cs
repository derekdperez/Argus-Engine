using System.Text.Json;
using System.Globalization;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.Workers.HttpRequester;

internal static class ProxyFingerprinting
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly BrowserTemplate[] BrowserTemplates =
    [
        new(
            "Chrome",
            "136.0.0.0",
            "Windows",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "\"Chromium\";v=\"136\", \"Google Chrome\";v=\"136\", \"Not.A/Brand\";v=\"99\"",
            "\"Windows\""),
        new(
            "Edge",
            "136.0.0.0",
            "Windows",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0",
            "\"Chromium\";v=\"136\", \"Microsoft Edge\";v=\"136\", \"Not.A/Brand\";v=\"99\"",
            "\"Windows\""),
        new(
            "Chrome",
            "136.0.0.0",
            "macOS",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_4) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "\"Chromium\";v=\"136\", \"Google Chrome\";v=\"136\", \"Not.A/Brand\";v=\"99\"",
            "\"macOS\""),
        new(
            "Firefox",
            "138.0",
            "Windows",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0",
            null,
            null)
    ];

    private static readonly (int Width, int Height)[] Viewports =
    [
        (1920, 1080),
        (1536, 864),
        (1366, 768),
        (2560, 1440),
        (1600, 900)
    ];

    private static readonly string[] Languages =
    [
        "en-US,en;q=0.9",
        "en-GB,en;q=0.9",
        "en-US,en;q=0.8"
    ];

    private static readonly string[] RefererTemplates =
    [
        "https://www.google.com/",
        "https://www.bing.com/",
        "https://duckduckgo.com/",
        "https://{target-host}/"
    ];

    public static ProxyTargetFingerprintProfile CreateProfile(
        HttpRequestQueueSettings settings,
        HttpRequestQueueItem item,
        ProxyServerConfiguration proxy)
    {
        var targetKey = ProxyRouting.NormalizeAssignmentKey(item.DomainKey, item.RequestUrl);
        var browser = PickBrowser(settings);
        var viewport = Viewports[Random.Shared.Next(Viewports.Length)];
        var language = Languages[Random.Shared.Next(Languages.Length)];
        var headers = BuildBaseHeaders(browser, language, viewport.Width, targetKey);

        var refererTemplate = "direct";
        if (settings.SpoofReferer)
        {
            refererTemplate = RefererTemplates[Random.Shared.Next(RefererTemplates.Length)];
            if (refererTemplate != "direct")
            {
                headers["Referer"] = refererTemplate.Replace("{target-host}", targetKey, StringComparison.OrdinalIgnoreCase);
            }
        }

        var minDelay = Math.Clamp(settings.ProxyFingerprintMinDelayMs, 0, 60_000);
        var maxDelay = Math.Clamp(settings.ProxyFingerprintMaxDelayMs, minDelay, 120_000);

        return new ProxyTargetFingerprintProfile
        {
            ProxyId = string.IsNullOrWhiteSpace(proxy.Id) ? proxy.CacheKey : proxy.Id,
            ProxyName = string.IsNullOrWhiteSpace(proxy.Name) ? proxy.Host : proxy.Name,
            ProxyPublicIp = string.IsNullOrWhiteSpace(proxy.PublicIpAddress) ? null : proxy.PublicIpAddress.Trim(),
            TargetKey = targetKey,
            BrowserFamily = browser.Family,
            BrowserVersion = browser.Version,
            Platform = browser.Platform,
            AcceptLanguage = language,
            ViewportWidth = viewport.Width,
            ViewportHeight = viewport.Height,
            UserAgent = headers.TryGetValue("User-Agent", out var userAgent) ? userAgent : browser.UserAgent,
            RefererTemplate = refererTemplate,
            HeaderProfileJson = JsonSerializer.Serialize(headers, JsonOptions),
            DelayMinMs = minDelay,
            DelayMaxMs = maxDelay
        };
    }

    public static IReadOnlyList<KeyValuePair<string, string>> BuildHeaders(
        ProxyTargetFingerprintProfile profile,
        HttpRequestQueueSettings settings)
    {
        Dictionary<string, string> headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.HeaderProfileJson, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (!headers.ContainsKey("User-Agent") && !string.IsNullOrWhiteSpace(profile.UserAgent))
        {
            headers["User-Agent"] = profile.UserAgent;
        }

        if (!headers.ContainsKey("Accept-Language") && !string.IsNullOrWhiteSpace(profile.AcceptLanguage))
        {
            headers["Accept-Language"] = profile.AcceptLanguage;
        }

        ApplyCustomHeaders(settings.CustomHeadersJson, headers);

        var list = headers.ToList();
        if (settings.RandomizeHeaderOrder && list.Count > 1)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        return list;
    }

    public static int GetDelayMs(HttpRequestQueueSettings settings, ProxyTargetFingerprintProfile? profile)
    {
        if (profile is not null)
        {
            var min = Math.Clamp(profile.DelayMinMs, 0, 60_000);
            var max = Math.Clamp(profile.DelayMaxMs, min, 120_000);
            return Random.Shared.Next(min, max + 1);
        }

        if (!settings.UseRandomJitter)
        {
            return 0;
        }

        var jitterMin = Math.Clamp(settings.MinJitterMs, 0, 60_000);
        var jitterMax = Math.Clamp(settings.MaxJitterMs, jitterMin, 120_000);
        return Random.Shared.Next(jitterMin, jitterMax + 1);
    }

    private static BrowserTemplate PickBrowser(HttpRequestQueueSettings settings)
    {
        if (settings.RotateUserAgents)
        {
            var custom = ParseCustomUserAgents(settings.CustomUserAgentsJson);
            if (custom.Count > 0)
            {
                var ua = custom[Random.Shared.Next(custom.Count)];
                return new BrowserTemplate("Custom", "custom", "Unknown", ua, null, null);
            }
        }

        var weighted = Random.Shared.Next(100);
        return weighted switch
        {
            < 58 => BrowserTemplates[0],
            < 78 => BrowserTemplates[1],
            < 92 => BrowserTemplates[2],
            _ => BrowserTemplates[3]
        };
    }

    private static Dictionary<string, string> BuildBaseHeaders(BrowserTemplate browser, string language, int viewportWidth, string targetKey)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = browser.UserAgent,
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8",
            ["Accept-Language"] = language,
            ["Accept-Encoding"] = "gzip, deflate, br",
            ["Cache-Control"] = "max-age=0",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Sec-Fetch-User"] = "?1",
            ["Viewport-Width"] = viewportWidth.ToString(CultureInfo.InvariantCulture),
            ["Sec-CH-Viewport-Width"] = viewportWidth.ToString(CultureInfo.InvariantCulture),
            ["Origin"] = $"https://{targetKey}"
        };

        if (!string.IsNullOrWhiteSpace(browser.ChUa))
        {
            headers["sec-ch-ua"] = browser.ChUa;
            headers["sec-ch-ua-mobile"] = "?0";
        }

        if (!string.IsNullOrWhiteSpace(browser.ChPlatform))
        {
            headers["sec-ch-ua-platform"] = browser.ChPlatform;
        }

        if (Random.Shared.Next(100) < 12)
        {
            headers["DNT"] = "1";
        }

        return headers;
    }

    private static List<string> ParseCustomUserAgents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Take(512)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ApplyCustomHeaders(string? json, Dictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            if (custom is null)
            {
                return;
            }

            foreach (var (key, value) in custom)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                headers[key.Trim()] = value.Trim();
            }
        }
        catch (JsonException)
        {
        }
    }

    private sealed record BrowserTemplate(
        string Family,
        string Version,
        string Platform,
        string UserAgent,
        string? ChUa,
        string? ChPlatform);
}
