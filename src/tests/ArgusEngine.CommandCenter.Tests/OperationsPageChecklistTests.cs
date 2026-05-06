namespace ArgusEngine.CommandCenter.Tests;

public sealed class OperationsPageChecklistTests
{
    [Fact]
    public void OperationsPageKeepsInteractiveWorkerControlsWiredToBackendEndpoints()
    {
        var text = ReadOperationsPage();

        Assert.Contains("ReloadAllAsync", text);
        Assert.Contains("SetWorkerEnabledAsync", text);
        Assert.Contains("ApplyWorkerScaleInputAsync", text);
        Assert.Contains("/api/ops/snapshot", text);
        Assert.Contains("/api/workers/activity", text);
        Assert.Contains("/api/workers", text);
        Assert.Contains("/api/workers/scale", text);
        Assert.Contains("/api/workers/{Uri.EscapeDataString(workerKey)}/scale", text);
        Assert.Contains("confirm", text);
        Assert.Contains("ReadResponseMessageAsync", text);
        Assert.Contains("/api/ops/overview", text);
    }

    [Fact]
    public void OperationsPageSurfacesOperatorFeedbackAndScaleValidation()
    {
        var text = ReadOperationsPage();

        Assert.Contains("class=\"alert\"", text);
        Assert.Contains("_statusMessage", text);
        Assert.Contains("Live event updates enabled", text);
        Assert.Contains("WorkerScaleSummary", text);
        Assert.Contains("Math.Max(0, desiredCount)", text);
        Assert.Contains("does not map to a scalable ECS worker service", text);
        Assert.Contains("desired count updated", text);
    }

    [Fact]
    public void OperationsPageShowsTargetActionsAndRemovesRetiredSections()
    {
        var text = ReadOperationsPage();

        Assert.Contains("Enum Subdomains", text);
        Assert.Contains("Spider Asset", text);
        Assert.Contains("QueueTargetEnumAsync", text);
        Assert.Contains("QueueTargetSpiderAsync", text);
        Assert.DoesNotContain("Tool controls", text);
        Assert.DoesNotContain("EC2 Workers", text);
        Assert.DoesNotContain("Last asset created", text);
        Assert.DoesNotContain("Last worker event", text);
        Assert.DoesNotContain("HTTP failed 1m / 1h / 24h", text);
        Assert.DoesNotContain("HTTP sent 1m / 1h / 24h", text);
    }

    [Fact]
    public void OperationsPageKeepsHttpQueueSimpleAndAddsStateShortcuts()
    {
        var text = ReadOperationsPage();

        Assert.Contains("Pause new requests", text);
        Assert.Contains("SetQueueStateFilter(\"Failed\")", text);
        Assert.Contains("SetQueueStateFilter(\"Succeeded\")", text);
        Assert.DoesNotContain("Global req/min", text);
        Assert.DoesNotContain("Per-domain req/min", text);
        Assert.DoesNotContain("Max concurrency", text);
        Assert.Contains("private int _queueGlobalRequestsPerMinute = 120_000;", text);
        Assert.Contains("private int _queuePerDomainRequestsPerMinute = 120;", text);
        Assert.Contains("private int _queueRequestTimeoutSeconds = 30;", text);
    }

    private static string ReadOperationsPage()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Components", "Pages", "OpsRadzen.razor");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArgusEngine.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
