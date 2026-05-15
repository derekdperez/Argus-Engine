using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Services;

public sealed class GcpCloudRunClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<GcpCloudRunClient> logger)
{
    private readonly HttpClient _http = httpFactory.CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string[] WorkerSlugs = ["spider", "http-requester", "enum", "portscan", "highvalue", "techid"];
    private static readonly string[] WorkerLabels = ["Spider", "HttpRequester", "Enumeration", "PortScan", "HighValue", "TechId"];

    private string? _projectId;
    private string? _region;
    private string? _cachedToken;
    private DateTime _tokenExpiry;

    private string ProjectId => _projectId ??= config["GcpDeploy:ProjectId"] ?? config["GCP_PROJECT_ID"] ?? "";
    private string Region => _region ??= config["GcpDeploy:Region"] ?? config["GCP_REGION"] ?? "us-east1";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId);

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        var req = new HttpRequestMessage(HttpMethod.Get,
            "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token");
        req.Headers.Add("Metadata-Flavor", "Google");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        _cachedToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        _tokenExpiry = DateTime.UtcNow.AddSeconds(
            doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return _cachedToken;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var url = $"https://us-east1-run.googleapis.com/v2/projects/{ProjectId}/locations/{Region}/{path}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    public async Task<List<GcpWorkerStatus>> GetWorkerStatusesAsync(CancellationToken ct)
    {
        var result = new List<GcpWorkerStatus>();
        if (!IsConfigured) return result;
        try
        {
            var request = await CreateRequestAsync(HttpMethod.Get, "services", ct: ct);
            using var resp = await _http.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var services = json.TryGetProperty("services", out var svc) ? svc : json.TryGetProperty("items", out var items) ? items : default;
            if (services.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in services.EnumerateArray())
                {
                    var name = s.TryGetProperty("name", out var n) ? n.GetString()?.Split('/').Last() ?? "" : "";
                    var uid = s.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : name;
                    var url = s.TryGetProperty("uri", out var uri) ? uri.GetString() : null;
                    var template = s.TryGetProperty("template", out var t) ? t : default;
                    var annotations = template.ValueKind == JsonValueKind.Object && template.TryGetProperty("annotations", out var a) ? a : default;
                    var minStr = annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty("autoscaling.knative.dev/minScale", out var minEl) ? minEl.GetString() : "0";
                    var maxStr = annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty("autoscaling.knative.dev/maxScale", out var maxEl) ? maxEl.GetString() : "0";
                    var status = s.TryGetProperty("status", out var st) ? st : default;
                    var conditions = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("conditions", out var conds) ? conds : default;
                    var ready = conditions.ValueKind == JsonValueKind.Array && conditions.EnumerateArray().Any(c =>
                        c.TryGetProperty("type", out var tpe) && tpe.GetString() == "Ready" &&
                        c.TryGetProperty("status", out var rdy) && rdy.GetString() == "True");

                    result.Add(new GcpWorkerStatus(name, url ?? "", ready ? "active" : "inactive", int.TryParse(minStr, out var mn) ? mn : 1, int.TryParse(maxStr, out var mx) ? mx : 1));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list Cloud Run services");
        }
        return result;
    }

    public async Task<GcpWorkerStatus?> DeployWorkerAsync(string slug, int minInstances = 1, int maxInstances = 2, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        var serviceName = $"argus-worker-{slug}";
        var imageTag = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var image = $"{Region}-docker.pkg.dev/{ProjectId}/argus-engine/argus-engine/worker-{slug}:{imageTag}";

        var body = new
        {
            apiVersion = "serving.knative.dev/v1",
            kind = "Service",
            metadata = new
            {
                name = serviceName,
                annotations = new Dictionary<string, string>
                {
                    ["autoscaling.knative.dev/minScale"] = minInstances.ToString(CultureInfo.InvariantCulture),
                    ["autoscaling.knative.dev/maxScale"] = maxInstances.ToString(CultureInfo.InvariantCulture),
                    ["run.googleapis.com/cpu-throttling"] = "false"
                }
            },
            spec = new
            {
                template = new
                {
                    spec = new
                    {
                        containerConcurrency = 4,
                        timeoutSeconds = 3600,
                        containers = new[]
                        {
                            new
                            {
                                image,
                                ports = new[] { new { containerPort = 8080 } },
                                env = new[]
                                {
                                    new { name = "PORT", value = "8080" },
                                    new { name = "ConnectionStrings__Postgres", value = $"Host={config["GcpDeploy:HostPublicAddress"] ?? "34.148.132.67"};Port=5432;Database=argus_engine;Username=argus;Password=argus" },
                                    new { name = "ConnectionStrings__Redis", value = $"{(config["GcpDeploy:HostPublicAddress"] ?? "34.148.132.67")}:6379" },
                                    new { name = "RabbitMq__Host", value = config["GcpDeploy:HostPublicAddress"] ?? "34.148.132.67" },
                                    new { name = "RabbitMq__Username", value = "argus" },
                                    new { name = "RabbitMq__Password", value = "argus" },
                                    new { name = "RabbitMq__VirtualHost", value = "/" },
                                    new { name = "RabbitMq__ManagementUrl", value = $"http://{config["GcpDeploy:HostPublicAddress"] ?? "34.148.132.67"}:15672" },
                                    new { name = "Argus__SkipStartupDatabase", value = "true" },
                                    new { name = "ARGUS_SKIP_STARTUP_DATABASE", value = "1" }
                                },
                                resources = new
                                {
                                    limits = new Dictionary<string, string>
                                    {
                                        ["cpu"] = "1",
                                        ["memory"] = "1Gi"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        try
        {
            var request = await CreateRequestAsync(HttpMethod.Put, $"services/{serviceName}", body, ct);
            using var resp = await _http.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();
            return new GcpWorkerStatus(serviceName, $"https://{serviceName}-{ProjectId}.{Region}.run.app", "active", minInstances, maxInstances);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deploy worker {Slug}", slug);
            return null;
        }
    }

    public async Task<bool> ScaleWorkerAsync(string slug, int minInstances, int maxInstances, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        var serviceName = $"argus-worker-{slug}";

        try
        {
            var getReq = await CreateRequestAsync(HttpMethod.Get, $"services/{serviceName}", ct: ct);
            using var getResp = await _http.SendAsync(getReq, ct);
            if (!getResp.IsSuccessStatusCode) return false;
            var existing = await getResp.Content.ReadFromJsonAsync<JsonElement>(ct);

            var patch = new
            {
                template = new
                {
                    metadata = new
                    {
                        annotations = new Dictionary<string, string>
                        {
                            ["autoscaling.knative.dev/minScale"] = minInstances.ToString(CultureInfo.InvariantCulture),
                            ["autoscaling.knative.dev/maxScale"] = maxInstances.ToString(CultureInfo.InvariantCulture)
                        }
                    }
                }
            };

            var patchReq = await CreateRequestAsync(HttpMethod.Patch, $"services/{serviceName}", patch, ct);
            patchReq.Headers.Add("X-Cloud-Run-Patch", "{\"template\":{\"metadata\":{\"annotations\":true}}}");
            using var patchResp = await _http.SendAsync(patchReq, ct);
            return patchResp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to scale worker {Slug}", slug);
            return false;
        }
    }
}

public sealed record GcpWorkerStatus(string Name, string Url, string Status, int MinInstances, int MaxInstances);
