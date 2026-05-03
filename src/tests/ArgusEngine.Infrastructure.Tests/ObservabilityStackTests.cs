using System.Text.Json;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class ObservabilityStackTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void LocalObservabilityComposeStackIsPresent()
    {
        var composePath = Path.Combine(Root, "deploy", "docker-compose.observability.yml");
        var compose = File.ReadAllText(composePath);

        Assert.Contains("otel/opentelemetry-collector-contrib:0.151.0", compose);
        Assert.Contains("prom/prometheus:v3.11.3", compose);
        Assert.Contains("grafana/grafana:13.0.1", compose);
        Assert.Contains("OpenTelemetry__OtlpEndpoint", compose);
    }

    [Fact]
    public void GrafanaDashboardContainsRequiredArgusPanels()
    {
        var dashboardPath = Path.Combine(
            Root,
            "deploy",
            "observability",
            "grafana",
            "dashboards",
            "argus-engine-overview.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(dashboardPath));
        var root = doc.RootElement;

        Assert.Equal("argus-engine-overview", root.GetProperty("uid").GetString());

        var serialized = root.ToString();
        Assert.Contains("argus_http_queue_depth", serialized);
        Assert.Contains("argus_outbox_depth", serialized);
        Assert.Contains("argus_asset_admission_decisions_total", serialized);
        Assert.Contains("argus_data_retention_deleted_rows_total", serialized);
    }
}
