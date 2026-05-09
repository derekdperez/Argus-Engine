using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArgusEngine.CommandCenter.Web.Clients;

public sealed class LocalDockerClient : IDisposable
{
    private readonly HttpClient _client;

    public LocalDockerClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
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
        var filters = JsonSerializer.Serialize(new { label = new[] { $"com.docker.compose.service={serviceName}" } });
        var response = await _client.GetAsync($"/v1.41/containers/json?filters={Uri.EscapeDataString(filters)}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        
        var list = new List<DockerContainerDto>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            list.Add(new DockerContainerDto(
                element.GetProperty("Id").GetString()!,
                element.GetProperty("Names")[0].GetString()!,
                element.GetProperty("State").GetString()!));
        }

        return list.OrderBy(c => c.Name).ToList();
    }

    public async Task KillContainerAsync(string id, CancellationToken ct)
    {
        var response = await _client.PostAsync($"/v1.41/containers/{id}/kill", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var deleteResponse = await _client.DeleteAsync($"/v1.41/containers/{id}?force=true", ct).ConfigureAwait(false);
        deleteResponse.EnsureSuccessStatusCode();
    }

    public async Task ScaleUpAsync(string serviceName, CancellationToken ct)
    {
        var containers = await GetContainersAsync(serviceName, ct);
        if (containers.Count == 0) throw new InvalidOperationException("Cannot scale up: no existing container to duplicate.");
        
        var templateId = containers.Last().Id;
        var templateRes = await _client.GetAsync($"/v1.41/containers/{templateId}/json", ct);
        templateRes.EnsureSuccessStatusCode();
        
        var templateJson = await templateRes.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(templateJson);
        var root = doc.RootElement;
        
        var config = JsonSerializer.Deserialize<Dictionary<string, object>>(root.GetProperty("Config").GetRawText());
        var hostConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(root.GetProperty("HostConfig").GetRawText());
        var endpointsConfig = root.GetProperty("NetworkSettings").GetProperty("Networks").GetRawText();
        var networkConfig = new Dictionary<string, object> { ["EndpointsConfig"] = JsonSerializer.Deserialize<Dictionary<string, object>>(endpointsConfig) };

        // Find the next container number
        int maxNumber = 0;
        foreach (var c in containers)
        {
            var match = System.Text.RegularExpressions.Regex.Match(c.Name, @"-(\d+)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num)) maxNumber = Math.Max(maxNumber, num);
        }
        var nextNumber = maxNumber + 1;
        var newName = $"argus-engine-{serviceName}-{nextNumber}";

        // Update labels
        var labelsObj = ((JsonElement)config["Labels"]).Clone();
        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelsObj.GetRawText());
        labels["com.docker.compose.container-number"] = nextNumber.ToString();
        config["Labels"] = labels;
        // Hostname needs to be reset
        config.Remove("Hostname");

        var createReq = new
        {
            Name = newName,
            Config = config,
            HostConfig = hostConfig,
            NetworkingConfig = networkConfig
        };

        var content = new StringContent(JsonSerializer.Serialize(new 
        { 
            Hostname = "",
            Domainname = "",
            User = config.ContainsKey("User") ? config["User"] : "",
            AttachStdin = false,
            AttachStdout = false,
            AttachStderr = false,
            Tty = false,
            OpenStdin = false,
            StdinOnce = false,
            Env = config.ContainsKey("Env") ? config["Env"] : null,
            Cmd = config.ContainsKey("Cmd") ? config["Cmd"] : null,
            Image = config.ContainsKey("Image") ? config["Image"] : null,
            Labels = labels,
            HostConfig = hostConfig,
            NetworkingConfig = networkConfig
        }), System.Text.Encoding.UTF8, "application/json");

        var createRes = await _client.PostAsync($"/v1.41/containers/create?name={Uri.EscapeDataString(newName)}", content, ct);
        var createJson = await createRes.Content.ReadAsStringAsync(ct);
        createRes.EnsureSuccessStatusCode();
        
        var newId = JsonDocument.Parse(createJson).RootElement.GetProperty("Id").GetString();
        
        var startRes = await _client.PostAsync($"/v1.41/containers/{newId}/start", null, ct);
        startRes.EnsureSuccessStatusCode();
    }
}

public record DockerContainerDto(string Id, string Name, string State);
