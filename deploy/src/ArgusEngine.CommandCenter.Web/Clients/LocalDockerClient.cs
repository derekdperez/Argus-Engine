using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.CommandCenter.Web.Clients;

public sealed class LocalDockerClient : IDisposable
{
    private const string DockerApiVersion = "v1.41";
    private const int DefaultErrorContextLines = 15;
    private const int DefaultErrorTailLines = 2500;
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly string[] ErrorNeedles =
    [
        " fail:",
        " error:",
        " crit:",
        " fatal",
        " emergency",
        " unhandled exception",
        " exception was thrown",
        "exception:",
        " system.invalidoperationexception:",
        " system.argumentexception:",
        " system.nullreferenceexception:",
        " system.npgsql",
        " npgsql.",
        " microsoft.aspnetcore.server.kestrel[13]",
        " microsoft.aspnetcore.diagnostics.exceptionhandlermiddleware",
        " circuit host terminated",
        " circuit will be terminated",
        " deadlock detected",
        " broker unreachable",
        " missed heartbeats",
        " connection refused",
        " connection reset",
        " relation \"",
        " does not exist",
        " status code 500",
        " 500 ",
        " failed",
        "fail:"
    ];

    private static readonly string[] IgnoredErrorNeedles =
    [
        "--- end of inner exception stack trace ---",
        "--- end of stack trace from previous location ---",
        "background saving terminated with success",
        "db saved on disk",
        "warning memory overcommit must be enabled"
    ];

    private readonly HttpClient _client;

    public LocalDockerClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public async Task<List<DockerContainerDto>> GetContainersAsync(string serviceName, CancellationToken ct)
    {
        var filters = JsonSerializer.Serialize(new
        {
            label = new[]
            {
                $"com.docker.compose.service={serviceName}"
            }
        });

        var response = await _client.GetAsync(
                $"/{DockerApiVersion}/containers/json?all=true&filters={Uri.EscapeDataString(filters)}",
                ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var list = new List<DockerContainerDto>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
            var name = element.TryGetProperty("Names", out var namesProp) && namesProp.ValueKind == JsonValueKind.Array && namesProp.GetArrayLength() > 0
                ? namesProp[0].GetString()
                : id;
            var state = element.TryGetProperty("State", out var stateProp) ? stateProp.GetString() : null;

            if (!string.IsNullOrWhiteSpace(id))
            {
                list.Add(new DockerContainerDto(id, name ?? id, state ?? "unknown"));
            }
        }

        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<DockerContainerDto>> GetComposeContainersAsync(CancellationToken ct)
    {
        var filters = JsonSerializer.Serialize(new
        {
            label = new[]
            {
                "com.docker.compose.project=argus-engine"
            }
        });

        var response = await _client.GetAsync(
                $"/{DockerApiVersion}/containers/json?all=true&filters={Uri.EscapeDataString(filters)}",
                ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var list = new List<DockerContainerDto>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
            var name = element.TryGetProperty("Names", out var namesProp) && namesProp.ValueKind == JsonValueKind.Array && namesProp.GetArrayLength() > 0
                ? namesProp[0].GetString()
                : id;
            var state = element.TryGetProperty("State", out var stateProp) ? stateProp.GetString() : null;
            var labels = ReadLabels(element);
            labels.TryGetValue("com.docker.compose.service", out var serviceName);

            if (!string.IsNullOrWhiteSpace(id))
            {
                list.Add(new DockerContainerDto(id, name ?? id, state ?? "unknown", serviceName));
            }
        }

        return list
            .OrderBy(c => c.ServiceName ?? c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SystemError>> GetDeploymentSystemErrorsAsync(
        int tailLines = DefaultErrorTailLines,
        int contextLines = DefaultErrorContextLines,
        CancellationToken ct = default)
    {
        var containers = await GetComposeContainersAsync(ct).ConfigureAwait(false);
        var errors = new List<SystemError>();

        foreach (var container in containers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var component = NormalizeComponent(container.ServiceName, container.Name);
                var logText = await GetContainerLogsAsync(container.Id, tailLines, ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(logText))
                {
                    continue;
                }

                errors.AddRange(ExtractErrorsFromLogs(container, component, logText, Math.Max(15, contextLines)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new SystemError
                {
                    Id = CreateStableGuid($"docker-log-read:{container.Id}:{ex.GetType().FullName}:{ex.Message}"),
                    Timestamp = DateTimeOffset.UtcNow,
                    Component = NormalizeComponent(container.ServiceName, container.Name),
                    MachineName = container.Name,
                    LogLevel = "Error",
                    LoggerName = "docker-compose-logs",
                    Message = $"Failed to read Docker logs for {container.Name}: {ex.Message}",
                    Exception = ex.ToString(),
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        source = "docker-api",
                        containerId = ShortId(container.Id),
                        container.Name,
                        container.ServiceName,
                        container.State
                    })
                });
            }
        }

        return errors
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    public async Task KillContainerAsync(string id, CancellationToken ct)
    {
        var response = await _client.PostAsync($"/{DockerApiVersion}/containers/{id}/kill", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var deleteResponse = await _client.DeleteAsync($"/{DockerApiVersion}/containers/{id}?force=true", ct).ConfigureAwait(false);
        deleteResponse.EnsureSuccessStatusCode();
    }

    public async Task ScaleUpAsync(string serviceName, CancellationToken ct)
    {
        var containers = await GetContainersAsync(serviceName, ct).ConfigureAwait(false);

        if (containers.Count == 0)
        {
            throw new InvalidOperationException("Cannot scale up: no existing container to duplicate.");
        }

        var templateId = containers.Last().Id;
        var templateRes = await _client.GetAsync($"/{DockerApiVersion}/containers/{templateId}/json", ct).ConfigureAwait(false);
        templateRes.EnsureSuccessStatusCode();

        var templateJson = await templateRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(templateJson);

        var root = doc.RootElement;
        var config = JsonSerializer.Deserialize<Dictionary<string, object?>>(root.GetProperty("Config").GetRawText())
            ?? new Dictionary<string, object?>();

        var hostConfig = JsonSerializer.Deserialize<Dictionary<string, object?>>(root.GetProperty("HostConfig").GetRawText())
            ?? new Dictionary<string, object?>();

        var endpointsConfig = root.GetProperty("NetworkSettings").GetProperty("Networks").GetRawText();

        var networkConfig = new Dictionary<string, object?>
        {
            ["EndpointsConfig"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(endpointsConfig)
        };

        var maxNumber = 0;

        foreach (var c in containers)
        {
            var match = Regex.Match(c.Name, @"-(\d+)$");

            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
            {
                maxNumber = Math.Max(maxNumber, num);
            }
        }

        var nextNumber = maxNumber + 1;
        var newName = $"argus-engine-{serviceName}-{nextNumber}";

        var labels = ReadObjectDictionary(config.TryGetValue("Labels", out var labelsObj) ? labelsObj : null);
        labels["com.docker.compose.container-number"] = nextNumber.ToString(CultureInfo.InvariantCulture);
        config["Labels"] = labels;

        config.Remove("Hostname");

        var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                Hostname = "",
                Domainname = "",
                User = config.TryGetValue("User", out var user) ? user : "",
                AttachStdin = false,
                AttachStdout = false,
                AttachStderr = false,
                Tty = false,
                OpenStdin = false,
                StdinOnce = false,
                Env = config.TryGetValue("Env", out var env) ? env : null,
                Cmd = config.TryGetValue("Cmd", out var cmd) ? cmd : null,
                Image = config.TryGetValue("Image", out var image) ? image : null,
                Labels = labels,
                HostConfig = hostConfig,
                NetworkingConfig = networkConfig
            }),
            Encoding.UTF8,
            "application/json");

        var createRes = await _client.PostAsync(
                $"/{DockerApiVersion}/containers/create?name={Uri.EscapeDataString(newName)}",
                content,
                ct)
            .ConfigureAwait(false);

        var createJson = await createRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        createRes.EnsureSuccessStatusCode();

        using var createDoc = JsonDocument.Parse(createJson);
        var newId = createDoc.RootElement.GetProperty("Id").GetString();

        var startRes = await _client.PostAsync($"/{DockerApiVersion}/containers/{newId}/start", null, ct).ConfigureAwait(false);
        startRes.EnsureSuccessStatusCode();
    }

    private async Task<string> GetContainerLogsAsync(string id, int tailLines, CancellationToken ct)
    {
        var response = await _client.GetAsync(
                $"/{DockerApiVersion}/containers/{id}/logs?stdout=true&stderr=true&timestamps=true&tail={Math.Max(1, tailLines).ToString(CultureInfo.InvariantCulture)}",
                ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return DecodeDockerLogPayload(payload);
    }

    private static IReadOnlyList<SystemError> ExtractErrorsFromLogs(
        DockerContainerDto container,
        string component,
        string logText,
        int contextLines)
    {
        var lines = logText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(CleanLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            return Array.Empty<SystemError>();
        }

        var matchIndexes = lines
            .Select((line, index) => new { line, index })
            .Where(item => IsErrorLine(item.line))
            .Select(item => item.index)
            .ToList();

        if (matchIndexes.Count == 0)
        {
            return Array.Empty<SystemError>();
        }

        var blocks = new List<(int Start, int End, List<int> Matches)>();

        foreach (var matchIndex in matchIndexes)
        {
            var start = Math.Max(0, matchIndex - contextLines);
            var end = Math.Min(lines.Count - 1, matchIndex + contextLines);

            if (blocks.Count > 0 && start <= blocks[^1].End + 1)
            {
                var last = blocks[^1];
                last.End = Math.Max(last.End, end);
                last.Matches.Add(matchIndex);
                blocks[^1] = last;
            }
            else
            {
                blocks.Add((start, end, new List<int> { matchIndex }));
            }
        }

        var errors = new List<SystemError>();

        foreach (var block in blocks)
        {
            var matchedLine = lines[block.Matches[0]];
            var timestamp = TryParseDockerTimestamp(matchedLine) ?? DateTimeOffset.UtcNow;
            var contextBlock = string.Join(Environment.NewLine, lines.Skip(block.Start).Take(block.End - block.Start + 1));
            var relativeLine = block.Matches[0] - block.Start + 1;

            errors.Add(new SystemError
            {
                Id = CreateStableGuid($"{container.Id}:{block.Start}:{block.End}:{matchedLine}"),
                Timestamp = timestamp,
                Component = component,
                MachineName = container.Name,
                LogLevel = DetermineLogLevel(matchedLine),
                LoggerName = "docker-compose-logs",
                Message = StripTimestamp(matchedLine),
                Exception = contextBlock,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    source = "docker-compose-container-logs",
                    contextLinesBeforeAndAfter = contextLines,
                    matchedRelativeLine = relativeLine,
                    matchedLogLine = matchedLine,
                    containerId = ShortId(container.Id),
                    container.Name,
                    container.ServiceName,
                    container.State,
                    block.Start,
                    block.End,
                    matchedIndexes = block.Matches
                })
            });
        }

        return errors;
    }

    private static string DecodeDockerLogPayload(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        var index = 0;
        var parsedFrames = 0;

        while (index + 8 <= payload.Length)
        {
            var streamType = payload[index];
            var frameLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(index + 4, 4));

            if ((streamType is not (1 or 2)) || frameLength < 0 || index + 8 + frameLength > payload.Length)
            {
                break;
            }

            output.Append(Encoding.UTF8.GetString(payload, index + 8, frameLength));
            index += 8 + frameLength;
            parsedFrames++;
        }

        if (parsedFrames > 0 && index == payload.Length)
        {
            return output.ToString();
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static bool IsErrorLine(string line)
    {
        var normalized = line.Trim().ToLowerInvariant();

        if (IgnoredErrorNeedles.Any(needle => normalized.Contains(needle, StringComparison.Ordinal)))
        {
            return false;
        }

        if (normalized.Contains(" 404 ", StringComparison.Ordinal) ||
            normalized.Contains("status code 404", StringComparison.Ordinal))
        {
            return normalized.Contains("/api/", StringComparison.Ordinal);
        }

        if (normalized.Contains("exception", StringComparison.Ordinal) &&
            !normalized.Contains("unhandled exception", StringComparison.Ordinal) &&
            !normalized.Contains("exception was thrown", StringComparison.Ordinal) &&
            !Regex.IsMatch(normalized, @"[a-z0-9_.]+exception:", RegexOptions.CultureInvariant))
        {
            return false;
        }

        return ErrorNeedles.Any(needle => normalized.Contains(needle, StringComparison.Ordinal)) ||
            Regex.IsMatch(normalized, @"[a-z0-9_.]+exception:", RegexOptions.CultureInvariant);
    }

    private static string DetermineLogLevel(string line)
    {
        var normalized = line.ToLowerInvariant();

        if (normalized.Contains("critical", StringComparison.Ordinal) ||
            normalized.Contains(" fatal", StringComparison.Ordinal) ||
            normalized.Contains(" emergency", StringComparison.Ordinal))
        {
            return "Critical";
        }

        if (normalized.Contains("warning", StringComparison.Ordinal) ||
            normalized.Contains(" warn:", StringComparison.Ordinal) ||
            normalized.Contains("missed heartbeats", StringComparison.Ordinal))
        {
            return "Warning";
        }

        return "Error";
    }

    private static string CleanLine(string line)
    {
        var cleaned = AnsiEscapeRegex.Replace(line, string.Empty)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .TrimEnd();

        return cleaned;
    }

    private static string StripTimestamp(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length > 31 &&
            trimmed[4] == '-' &&
            trimmed[7] == '-' &&
            trimmed[10] == 'T')
        {
            var firstSpace = trimmed.IndexOf(' ');

            if (firstSpace > 0 && firstSpace < trimmed.Length - 1)
            {
                return trimmed[(firstSpace + 1)..].Trim();
            }
        }

        return trimmed;
    }

    private static DateTimeOffset? TryParseDockerTimestamp(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length < 20 || trimmed[4] != '-' || trimmed[7] != '-' || trimmed[10] != 'T')
        {
            return null;
        }

        var firstSpace = trimmed.IndexOf(' ');
        var raw = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        var zIndex = raw.IndexOf('Z');

        if (zIndex > 0)
        {
            var dotIndex = raw.IndexOf('.');

            if (dotIndex > 0 && zIndex - dotIndex > 8)
            {
                raw = string.Concat(raw.AsSpan(0, dotIndex + 8), "Z");
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadLabels(JsonElement element)
    {
        if (!element.TryGetProperty("Labels", out var labelsProp) || labelsProp.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return labelsProp
            .EnumerateObject()
            .ToDictionary(
                prop => prop.Name,
                prop => prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadObjectDictionary(object? value)
    {
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            return json
                .EnumerateObject()
                .ToDictionary(
                    prop => prop.Name,
                    prop => prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
        }

        if (value is Dictionary<string, string> typed)
        {
            return new Dictionary<string, string>(typed, StringComparer.OrdinalIgnoreCase);
        }

        if (value is Dictionary<string, object?> objectMap)
        {
            return objectMap.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeComponent(string? serviceName, string containerName)
    {
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            return serviceName.Trim();
        }

        var name = containerName.Trim().TrimStart('/');

        if (name.StartsWith("argus-engine-", StringComparison.OrdinalIgnoreCase))
        {
            name = name["argus-engine-".Length..];
        }

        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "";
        }

        return id.Length <= 12 ? id : id[..12];
    }

    private static Guid CreateStableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public record DockerContainerDto(string Id, string Name, string State, string? ServiceName = null);
