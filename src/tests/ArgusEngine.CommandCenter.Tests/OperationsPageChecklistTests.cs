namespace ArgusEngine.CommandCenter.Tests;

public sealed class OperationsPageChecklistTests
{
    [Fact]
    public void OperationsPageKeepsInteractiveWorkerControlsWiredToBackendEndpoints()
    {
        var text = ReadOperationsPage();

        Assert.Contains("@onclick=\"RefreshAsync\"", text);
        Assert.Contains("SetWorkerEnabledAsync(row, !row.Enabled)", text);
        Assert.Contains("ApplyScaleAsync(row)", text);
        Assert.Contains("/api/ops/snapshot", text);
        Assert.Contains("/api/ops/reliability-slo", text);
        Assert.Contains("/api/workers/capabilities", text);
        Assert.Contains("/api/workers/health", text);
        Assert.Contains("/api/workers/scale", text);
        Assert.Contains("/api/workers/scaling-settings", text);
        Assert.Contains("confirm", text);
        Assert.Contains("ReadResponseMessageAsync", text);
        Assert.DoesNotContain("/api/ops/overview", text);
    }

    [Fact]
    public void OperationsPageSurfacesOperatorFeedbackAndScaleValidation()
    {
        var text = ReadOperationsPage();

        Assert.Contains("role=\"status\"", text);
        Assert.Contains("role=\"alert\"", text);
        Assert.Contains("Last action", text);
        Assert.Contains("Degraded workers", text);
        Assert.Contains("ScaleValidationMessage", text);
        Assert.Contains("CanApplyScale(row)", text);
        Assert.Contains("Desired worker count must be zero or greater.", text);
        Assert.Contains("No scale change to apply.", text);
    }

    private static string ReadOperationsPage()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Components", "Pages", "Operations.razor");
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
