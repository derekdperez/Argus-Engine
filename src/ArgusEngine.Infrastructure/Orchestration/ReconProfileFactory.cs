using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArgusEngine.Application.Orchestration;

namespace ArgusEngine.Infrastructure.Orchestration;

internal static class ReconProfileFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly string[] Languages = ["en-US,en;q=0.9", "en-GB,en;q=0.9", "en-US,en;q=0.8"];

    public static GeneratedReconProfile Create(
        ReconOrchestratorConfiguration configuration,
        Guid targetId,
        string subdomainKey,
        string machineKey,
        int profileIndex)
    {
        var seed = StableInt($"{targetId:N}|{subdomainKey}|profile:{profileIndex}");
        var deviceType = Pick(configuration.ReconProfileDeviceTypes, seed, 11, "desktop");
        var browser = PickBrowser(configuration, deviceType, seed);
        var os = PickOs(configuration, deviceType, browser, seed);
        var hardwareAge = configuration.ReconProfileHardwareAge <= 0
            ? 0
            : PositiveModulo(StableInt($"{seed}|hardware-age"), configuration.ReconProfileHardwareAge + 1);
        var language = Pick(Languages, seed, 17, "en-US,en;q=0.9");
        var viewport = PickViewport(deviceType, seed);
        var userAgent = BuildUserAgent(deviceType, browser, os, seed, hardwareAge);
        var headers = BuildHeaders(configuration, subdomainKey, deviceType, browser, os, language, viewport.Width, userAgent, seed);
        var headerOrderSeed = StableInt($"{seed}|header-order");

        if (configuration.RandomizeHeaderOrderEnabled)
        {
            headers = headers
                .OrderBy(kvp => StableInt($"{headerOrderSeed}|{kvp.Key}"))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        var minDelayMs = (int)Math.Round(Math.Max(0, configuration.RandomDelayMin) * 1000, MidpointRounding.AwayFromZero);
        var maxDelayMs = (int)Math.Round(Math.Max(configuration.RandomDelayMin, configuration.RandomDelayMax) * 1000, MidpointRounding.AwayFromZero);

        return new GeneratedReconProfile(
            profileIndex,
            deviceType,
            browser,
            os,
            hardwareAge,
            userAgent,
            language,
            headers,
            JsonSerializer.Serialize(headers, JsonOptions),
            headerOrderSeed,
            configuration.RandomDelayEnabled,
            minDelayMs,
            maxDelayMs,
            configuration.RequestsPerMinutePerSubdomain);
    }

    public static IReadOnlyList<KeyValuePair<string, string>> DeserializeHeaders(string? headersJson, int headerOrderSeed, bool randomize)
    {
        Dictionary<string, string> headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson ?? "{}", JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        IEnumerable<KeyValuePair<string, string>> ordered = headers;
        if (randomize)
        {
            ordered = ordered.OrderBy(kvp => StableInt($"{headerOrderSeed}|{kvp.Key}"));
        }

        return ordered.ToList();
    }

    private static Dictionary<string, string> BuildHeaders(
        ReconOrchestratorConfiguration configuration,
        string targetKey,
        string deviceType,
        string browser,
        string os,
        string language,
        int viewportWidth,
        string userAgent,
        int seed)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = userAgent,
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            ["Accept-Language"] = language,
            ["Accept-Encoding"] = "gzip, deflate, br",
            ["Cache-Control"] = "max-age=0",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none",
            ["Sec-Fetch-User"] = "?1",
            ["Viewport-Width"] = viewportWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sec-CH-Viewport-Width"] = viewportWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (browser is "chrome" or "safari")
        {
            headers["Origin"] = $"https://{targetKey}";
        }

        if (browser == "chrome")
        {
            var major = ChromeMajor(seed);
            headers["sec-ch-ua"] = $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", \"Not.A/Brand\";v=\"99\"";
            headers["sec-ch-ua-mobile"] = deviceType == "mobile" ? "?1" : "?0";
            headers["sec-ch-ua-platform"] = os switch
            {
                "windows" => "\"Windows\"",
                "android" => "\"Android\"",
                "ios" => "\"iOS\"",
                "chrome" or "chromeos" => "\"ChromeOS\"",
                _ => "\"Windows\""
            };
        }

        if (PositiveModulo(seed, 100) < 10)
        {
            headers["DNT"] = "1";
        }

        return headers;
    }

    private static string PickBrowser(ReconOrchestratorConfiguration configuration, string deviceType, int seed)
    {
        var allowed = configuration.ReconProfileBrowsers;
        if (deviceType == "mobile" && allowed.Contains("safari", StringComparer.OrdinalIgnoreCase) && PositiveModulo(seed, 100) < 35)
        {
            return "safari";
        }

        return Pick(allowed, seed, 23, "chrome");
    }

    private static string PickOs(ReconOrchestratorConfiguration configuration, string deviceType, string browser, int seed)
    {
        var allowed = configuration.ReconProfileOs;
        if (browser == "safari" && allowed.Contains("ios", StringComparer.OrdinalIgnoreCase))
        {
            return deviceType == "mobile" || deviceType == "tablet" ? "ios" : Pick(allowed, seed, 31, "ios");
        }

        if (deviceType == "mobile")
        {
            if (allowed.Contains("android", StringComparer.OrdinalIgnoreCase) && PositiveModulo(seed, 100) >= 35)
            {
                return "android";
            }

            if (allowed.Contains("ios", StringComparer.OrdinalIgnoreCase))
            {
                return "ios";
            }
        }

        return Pick(allowed, seed, 31, "windows");
    }

    private static (int Width, int Height) PickViewport(string deviceType, int seed)
    {
        var desktop = new[] { (1920, 1080), (1536, 864), (1366, 768), (1600, 900), (2560, 1440) };
        var tablet = new[] { (1180, 820), (1024, 768), (1366, 1024), (834, 1194) };
        var mobile = new[] { (390, 844), (414, 896), (393, 873), (360, 800), (430, 932) };

        return deviceType switch
        {
            "mobile" => mobile[PositiveModulo(seed, mobile.Length)],
            "tablet" => tablet[PositiveModulo(seed, tablet.Length)],
            _ => desktop[PositiveModulo(seed, desktop.Length)]
        };
    }

    private static string BuildUserAgent(string deviceType, string browser, string os, int seed, int hardwareAge)
    {
        var chrome = ChromeMajor(seed);
        var firefox = FirefoxMajor(seed);
        var safari = SafariVersion(seed);

        return browser switch
        {
            "firefox" when os == "android" => $"Mozilla/5.0 (Android 14; Mobile; rv:{firefox}.0) Gecko/{firefox}.0 Firefox/{firefox}.0",
            "firefox" => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{firefox}.0) Gecko/20100101 Firefox/{firefox}.0",
            "safari" when deviceType == "mobile" => $"Mozilla/5.0 (iPhone; CPU iPhone OS {Math.Max(15, 17 - Math.Min(hardwareAge, 2))}_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{safari} Mobile/15E148 Safari/604.1",
            "safari" => $"Mozilla/5.0 (iPad; CPU OS {Math.Max(15, 17 - Math.Min(hardwareAge, 2))}_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{safari} Mobile/15E148 Safari/604.1",
            "chrome" when os == "android" => $"Mozilla/5.0 (Linux; Android {Math.Max(10, 14 - Math.Min(hardwareAge, 4))}; Pixel {6 + PositiveModulo(seed, 3)}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chrome}.0.0.0 Mobile Safari/537.36",
            "chrome" when os is "chrome" or "chromeos" => $"Mozilla/5.0 (X11; CrOS x86_64 15699.72.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chrome}.0.0.0 Safari/537.36",
            _ => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chrome}.0.0.0 Safari/537.36"
        };
    }

    private static int ChromeMajor(int seed) => 124 + PositiveModulo(seed, 13);

    private static int FirefoxMajor(int seed) => 115 + PositiveModulo(seed, 24);

    private static string SafariVersion(int seed) => PositiveModulo(seed, 2) == 0 ? "17.0" : "16.6";

    private static string Pick(IReadOnlyList<string> values, int seed, int salt, string fallback)
    {
        if (values.Count == 0)
        {
            return fallback;
        }

        return values[PositiveModulo(seed + salt, values.Count)].Trim().ToLowerInvariant();
    }

    private static int PositiveModulo(int value, int modulo)
    {
        if (modulo <= 0)
        {
            return 0;
        }

        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    public static int StableInt(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return BitConverter.ToInt32(hash[..4]);
    }
}

internal sealed record GeneratedReconProfile(
    int ProfileIndex,
    string DeviceType,
    string Browser,
    string OperatingSystem,
    int HardwareAgeYears,
    string UserAgent,
    string AcceptLanguage,
    IReadOnlyDictionary<string, string> Headers,
    string HeadersJson,
    int HeaderOrderSeed,
    bool RandomDelayEnabled,
    int RandomDelayMinMs,
    int RandomDelayMaxMs,
    int RequestsPerMinutePerSubdomain);
