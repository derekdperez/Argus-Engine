using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

public static class RabbitMqStatusEndpoints
{
    public static IEndpointRouteBuilder MapRabbitMqStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/rabbitmq/status", async (IConfiguration config, CancellationToken ct) =>
        {
            var host = config["RabbitMq:ManagementUrl"] ?? config["RabbitMq__ManagementUrl"] ?? "http://rabbitmq:15672";
            var username = config["RabbitMq:Username"] ?? config["RabbitMq__Username"] ?? "argus";
            var password = config["RabbitMq:Password"] ?? config["RabbitMq__Password"] ?? "argus";

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(host), Timeout = TimeSpan.FromSeconds(10) };
                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

                var overviewTask = http.GetFromJsonAsync<JsonElement>("/api/overview", ct);
                var queuesTask = http.GetFromJsonAsync<List<JsonElement>>("/api/queues", ct);
                var consumersTask = http.GetFromJsonAsync<List<JsonElement>>("/api/consumers", ct);

                await Task.WhenAll(overviewTask, queuesTask, consumersTask);

                var overview = overviewTask.Result;
                var queues = queuesTask.Result ?? [];
                var consumers = consumersTask.Result ?? [];

                var queueStats = queues.Select(q => new
                {
                    name = Get(q, "name"),
                    messages = Long(q, "messages"),
                    ready = Long(q, "messages_ready"),
                    unacked = Long(q, "messages_unacknowledged"),
                    consumers = Int(q, "consumers"),
                    state = Get(q, "state")
                }).ToList();

                var consumerStats = consumers
                    .Select(c =>
                    {
                        var queueName = c.TryGetProperty("queue", out var qObj) && qObj.TryGetProperty("name", out var qn)
                            ? qn.GetString() ?? "?" : "?";
                        var channelName = c.TryGetProperty("channel_details", out var ch)
                            && ch.TryGetProperty("name", out var cn)
                            ? cn.GetString()?.Split('.').LastOrDefault() ?? "?" : "?";
                        var node = c.TryGetProperty("channel_details", out var nd)
                            && nd.TryGetProperty("node", out var nn)
                            ? nn.GetString() ?? "?" : "?";
                        var tag = Get(c, "consumer_tag");
                        return new { queueName, tag, channelName, node };
                    })
                    .GroupBy(x => x.queueName)
                    .Select(g => new
                    {
                        queue = g.Key,
                        count = g.Count(),
                        consumers = g.Select(x => new
                        {
                            tag = x.tag,
                            channel = x.channelName,
                            node = x.node
                        }).ToList()
                    }).ToList();

                return Results.Ok(new
                {
                    version = Get(overview, "rabbitmq_version"),
                    cluster = Get(overview, "cluster_name"),
                    queueCount = Int(overview, "object_totals", "queues"),
                    consumerCount = Int(overview, "object_totals", "consumers"),
                    totalMessages = Long(overview, "queue_totals", "messages"),
                    queues = queueStats,
                    consumers = consumerStats
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = $"Cannot connect to RabbitMQ management API: {ex.Message}", queues = Array.Empty<object>(), consumers = Array.Empty<object>() });
            }
        });

        return app;
    }

    private static string Get(JsonElement el, params string[] path)
    {
        foreach (var p in path)
        {
            if (el.ValueKind != JsonValueKind.Object) return "";
            if (!el.TryGetProperty(p, out el)) return "";
        }
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => el.ToString()
        };
    }

    private static long Long(JsonElement el, params string[] path)
    {
        foreach (var p in path)
        {
            if (el.ValueKind != JsonValueKind.Object) return 0;
            if (!el.TryGetProperty(p, out el)) return 0;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
        return 0;
    }

    private static int Int(JsonElement el, params string[] path)
    {
        foreach (var p in path)
        {
            if (el.ValueKind != JsonValueKind.Object) return 0;
            if (!el.TryGetProperty(p, out el)) return 0;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
        return 0;
    }
}
