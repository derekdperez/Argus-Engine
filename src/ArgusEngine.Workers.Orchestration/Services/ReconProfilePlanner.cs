using System.Security.Cryptography;
using System.Text;
using ArgusEngine.Workers.Orchestration.Configuration;
using ArgusEngine.Workers.Orchestration.State;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Workers.Orchestration.Services;

public interface IReconProfilePlanner
{
    ReconWorkerProfile GetOrCreateProfile(
        Guid targetId,
        string subdomain,
        string machineIdentity,
        IDictionary<string, ReconWorkerProfile> existingProfiles);
}

public sealed class ReconProfilePlanner : IReconProfilePlanner
{
    private readonly IOptionsMonitor<ReconOrchestratorOptions> _options;

    public ReconProfilePlanner(IOptionsMonitor<ReconOrchestratorOptions> options)
    {
        _options = options;
    }

    public ReconWorkerProfile GetOrCreateProfile(
        Guid targetId,
        string subdomain,
        string machineIdentity,
        IDictionary<string, ReconWorkerProfile> existingProfiles)
    {
        var key = ProfileKey(targetId, subdomain, machineIdentity);
        if (existingProfiles.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var options = _options.CurrentValue;
        var seed = StableSeed(key);
        var random = new Random(seed);
        var profile = BuildProfile(targetId, subdomain, machineIdentity, options, random);
        profile.ProfileId = key;
        existingProfiles[key] = profile;
        return profile;
    }

    private static ReconWorkerProfile BuildProfile(
        Guid targetId,
        string subdomain,
        string machineIdentity,
        ReconOrchestratorOptions options,
        Random random)
    {
        var allowedDevices = Normalize(options.ReconProfileDeviceTypes, ["mobile", "desktop", "tablet"]);
        var allowedBrowsers = Normalize(options.ReconProfileBrowsers, ["firefox", "chrome", "safari"]);
        var allowedOs = Normalize(options.ReconProfileOs, ["windows", "ios", "android", "chrome"]);
        var maxAge = Math.Clamp(options.ReconProfileHardwareAge, 1, 25);
        var newestChrome = 125 + Math.Max(0, DateTimeOffset.UtcNow.Year - 2024) * 8;
        var newestFirefox = 126 + Math.Max(0, DateTimeOffset.UtcNow.Year - 2024) * 8;
        var newestSafari = 17 + Math.Max(0, DateTimeOffset.UtcNow.Year - 2024);
        var oldestBrowser = Math.Max(90, newestChrome - maxAge * 8);
        var newestIos = 17 + Math.Max(0, DateTimeOffset.UtcNow.Year - 2024);
        var oldestIos = Math.Max(12, newestIos - maxAge);

        var viable = BuildViableProfiles(allowedDevices, allowedBrowsers, allowedOs).ToArray();
        var selection = viable[random.Next(viable.Length)];

        var browserVersion = selection.Browser switch
        {
            "firefox" => random.Next(Math.Max(100, newestFirefox - maxAge * 8), newestFirefox + 1),
            "safari" => random.Next(Math.Max(13, newestSafari - maxAge), newestSafari + 1),
            _ => random.Next(oldestBrowser, newestChrome + 1)
        };

        var osVersion = selection.OperatingSystem switch
        {
            "ios" => random.Next(oldestIos, newestIos + 1),
            "android" => random.Next(10, 15),
            "chrome" => random.Next(Math.Max(100, newestChrome - maxAge * 8), newestChrome + 1),
            _ => 10
        };

        var hardwareModel = SelectHardware(selection.DeviceType, selection.OperatingSystem, random);
        var userAgent = BuildUserAgent(selection.DeviceType, selection.Browser, selection.OperatingSystem, browserVersion, osVersion, hardwareModel);
        var headers = BuildHeaders(selection.DeviceType, selection.Browser, selection.OperatingSystem, browserVersion, userAgent);
        var headerOrder = headers.Keys.ToList();

        if (options.RandomizeHeaderOrderEnabled)
        {
            headerOrder = headerOrder.OrderBy(_ => random.Next()).ToList();
        }

        return new ReconWorkerProfile
        {
            TargetId = targetId,
            Subdomain = subdomain,
            MachineIdentity = machineIdentity,
            DeviceType = selection.DeviceType,
            Browser = selection.Browser,
            OperatingSystem = selection.OperatingSystem,
            HardwareModel = hardwareModel,
            BrowserMajorVersion = browserVersion,
            OsMajorVersion = osVersion,
            RequestsPerMinute = options.RequestsPerMinutePerSubdomain,
            RandomDelayEnabled = options.RandomDelayEnabled,
            RandomDelayMinSeconds = options.RandomDelayMin,
            RandomDelayMaxSeconds = options.RandomDelayMax,
            RandomizeHeaderOrderEnabled = options.RandomizeHeaderOrderEnabled,
            UserAgent = userAgent,
            Headers = headers,
            HeaderOrder = headerOrder,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static IReadOnlyList<ProfileTuple> BuildViableProfiles(
        IReadOnlyCollection<string> devices,
        IReadOnlyCollection<string> browsers,
        IReadOnlyCollection<string> oses)
    {
        var viable = new List<ProfileTuple>();

        void Add(string device, string browser, string os)
        {
            if (devices.Contains(device, StringComparer.OrdinalIgnoreCase)
                && browsers.Contains(browser, StringComparer.OrdinalIgnoreCase)
                && oses.Contains(os, StringComparer.OrdinalIgnoreCase))
            {
                viable.Add(new ProfileTuple(device, browser, os));
            }
        }

        Add("desktop", "chrome", "windows");
        Add("desktop", "firefox", "windows");
        Add("desktop", "chrome", "chrome");
        Add("mobile", "safari", "ios");
        Add("mobile", "chrome", "ios");
        Add("mobile", "chrome", "android");
        Add("mobile", "firefox", "android");
        Add("tablet", "safari", "ios");
        Add("tablet", "chrome", "android");
        Add("tablet", "firefox", "android");

        if (viable.Count == 0)
        {
            viable.Add(new ProfileTuple("desktop", "chrome", "windows"));
        }

        return viable;
    }

    private static Dictionary<string, string> BuildHeaders(
        string deviceType,
        string browser,
        string os,
        int browserMajorVersion,
        string userAgent)
    {
        var mobile = deviceType is "mobile" or "tablet";
        var platform = os switch
        {
            "windows" => "\"Windows\"",
            "android" => "\"Android\"",
            "ios" => "\"iOS\"",
            "chrome" => "\"Chrome OS\"",
            _ => "\"Windows\""
        };

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = userAgent,
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["Accept-Encoding"] = "gzip, deflate, br",
            ["Upgrade-Insecure-Requests"] = "1",
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Site"] = "none"
        };

        if (browser is "chrome")
        {
            headers["Sec-CH-UA"] = $"\"Chromium\";v=\"{browserMajorVersion}\", \"Google Chrome\";v=\"{browserMajorVersion}\", \"Not.A/Brand\";v=\"99\"";
            headers["Sec-CH-UA-Mobile"] = mobile ? "?1" : "?0";
            headers["Sec-CH-UA-Platform"] = platform;
        }

        return headers;
    }

    private static string BuildUserAgent(
        string deviceType,
        string browser,
        string os,
        int browserMajorVersion,
        int osMajorVersion,
        string hardwareModel)
    {
        return (deviceType, browser, os) switch
        {
            ("desktop", "firefox", "windows") =>
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{browserMajorVersion}.0) Gecko/20100101 Firefox/{browserMajorVersion}.0",
            ("desktop", "chrome", "chrome") =>
                $"Mozilla/5.0 (X11; CrOS x86_64 {osMajorVersion}.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserMajorVersion}.0.0.0 Safari/537.36",
            ("mobile", "safari", "ios") =>
                $"Mozilla/5.0 (iPhone; CPU iPhone OS {osMajorVersion}_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{Math.Max(13, osMajorVersion)}.0 Mobile/15E148 Safari/604.1",
            ("tablet", "safari", "ios") =>
                $"Mozilla/5.0 (iPad; CPU OS {osMajorVersion}_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{Math.Max(13, osMajorVersion)}.0 Mobile/15E148 Safari/604.1",
            ("mobile", "chrome", "ios") =>
                $"Mozilla/5.0 (iPhone; CPU iPhone OS {osMajorVersion}_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/{browserMajorVersion}.0.0.0 Mobile/15E148 Safari/604.1",
            ("mobile", "chrome", "android") =>
                $"Mozilla/5.0 (Linux; Android {osMajorVersion}; {hardwareModel}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserMajorVersion}.0.0.0 Mobile Safari/537.36",
            ("tablet", "chrome", "android") =>
                $"Mozilla/5.0 (Linux; Android {osMajorVersion}; {hardwareModel}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserMajorVersion}.0.0.0 Safari/537.36",
            ("mobile", "firefox", "android") =>
                $"Mozilla/5.0 (Android {osMajorVersion}; Mobile; rv:{browserMajorVersion}.0) Gecko/{browserMajorVersion}.0 Firefox/{browserMajorVersion}.0",
            ("tablet", "firefox", "android") =>
                $"Mozilla/5.0 (Android {osMajorVersion}; Tablet; rv:{browserMajorVersion}.0) Gecko/{browserMajorVersion}.0 Firefox/{browserMajorVersion}.0",
            _ =>
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserMajorVersion}.0.0.0 Safari/537.36"
        };
    }

    private static string SelectHardware(string deviceType, string os, Random random)
    {
        if (os == "android")
        {
            var models = new[]
            {
                "Pixel 8",
                "Pixel 7",
                "SM-G991U",
                "SM-S911U",
                "SM-X700",
                "ONEPLUS A6013"
            };
            return models[random.Next(models.Length)];
        }

        if (deviceType == "tablet" && os == "ios")
        {
            return "iPad";
        }

        if (deviceType == "mobile" && os == "ios")
        {
            return "iPhone";
        }

        if (os == "chrome")
        {
            return "Chromebook";
        }

        return "Win64";
    }

    private static List<string> Normalize(IEnumerable<string> values, IEnumerable<string> fallback)
    {
        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? fallback.ToList() : normalized;
    }

    private static string ProfileKey(Guid targetId, string subdomain, string machineIdentity)
    {
        var input = $"{targetId:N}:{subdomain.Trim().ToLowerInvariant()}:{machineIdentity.Trim().ToLowerInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "recon-" + Convert.ToHexString(bytes[..10]).ToLowerInvariant();
    }

    private static int StableSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }

    private readonly record struct ProfileTuple(string DeviceType, string Browser, string OperatingSystem);
}
