using System.Text.RegularExpressions;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class OriginalChecklistImplementationTests
{
    [Fact]
    public void DeploymentVersionIsIncrementedForThisPackage()
    {
        var root = FindRepositoryRoot();
        var targets = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
        var versionFile = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();

        Assert.Equal("2.6.1", versionFile);
        Assert.Contains("<ArgusEngineDeploymentVersion>2.6.1</ArgusEngineDeploymentVersion>", targets);
        Assert.Contains("<AssemblyVersion>2.6.1.0</AssemblyVersion>", targets);
        Assert.Contains("<FileVersion>2.6.1.0</FileVersion>", targets);
    }

    [Fact]
    public void AssetAdmissionUiAndApiArePresent()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Components", "Pages", "AssetAdmission.razor"));
        var endpoint = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Endpoints", "AssetAdmissionDecisionEndpoints.cs"));
        var layout = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("@page \"/asset-admission\"", page);
        Assert.Contains("/api/asset-admission-decisions", endpoint);
        Assert.Contains("/asset-admission", layout);
        Assert.DoesNotContain("N2 Argus Command Center", layout);
    }

    [Fact]
    public void GatekeeperDecisionPathsAreAuditable()
    {
        var root = FindRepositoryRoot();
        var orchestratorPath = Path.Combine(root, "src", "ArgusEngine.Application", "Gatekeeping", "GatekeeperOrchestrator.cs");
        Assert.True(File.Exists(orchestratorPath), "GatekeeperOrchestrator.cs must be extracted/instrumented.");

        var text = File.ReadAllText(orchestratorPath);

        foreach (var expected in new[]
        {
            "GatekeeperDisabled",
            "MaxDepthExceeded",
            "DuplicateCanonicalKey",
            "ScopeRejected",
            "PersistenceReturnedEmptyAssetId",
            "AcceptedNewAsset",
            "ExceptionDuringAdmission"
        })
        {
            Assert.Contains(expected, text);
        }

        Assert.Contains("IAssetAdmissionDecisionWriter", text);
        Assert.Contains("WriteAsync", text);
    }

    [Fact]
    public void HttpArtifactsAreStoredOutsideQueueRows()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.Workers.Spider", "HttpRequestQueueWorker.cs"));
        var item = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.Domain", "Entities", "HttpRequestQueueItem.cs"));
        var store = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.Infrastructure", "FileStore", "EfHttpArtifactStore.cs"));

        Assert.Contains("ResponseBodyBlobId", item);
        Assert.Contains("ResponseBodySha256", item);
        Assert.Contains("IHttpArtifactStore", worker);
        Assert.Contains("StoreTextAsync", worker);
        Assert.Contains("SHA256", store);
        Assert.DoesNotMatch(new Regex(@"item\.ResponseBody\s*=\s*snapshot\.ResponseBody"), worker);
    }

    [Fact]
    public void RetentionPartitionAndOutboxObservabilityArePresent()
    {
        var root = FindRepositoryRoot();
        var retention = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.Infrastructure", "DataRetention", "DataRetentionWorker.cs"));
        var partition = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.Infrastructure", "DataRetention", "PostgresPartitionMaintenanceService.cs"));
        var outbox = File.ReadAllText(Path.Combine(root, "src", "ArgusEngine.Infrastructure", "Messaging", "OutboxDispatcherWorker.cs"));

        Assert.Contains("ExecuteDeleteAsync", retention);
        Assert.Contains("archived_outbox_messages", retention);
        Assert.Contains("bus_journal_", partition);
        Assert.Contains("ArgusMeters.OutboxDispatched", outbox);
        Assert.Contains("ArgusMeters.OutboxDeadLettered", outbox);
        Assert.Contains("ArgusTracing.Source.StartActivity(\"outbox.dispatch\")", outbox);
    }

    [Fact]
    public void ObservabilityIsWiredIntoExecutables()
    {
        var root = FindRepositoryRoot();
        foreach (var path in new[]
        {
            Path.Combine("src", "ArgusEngine.CommandCenter", "Startup", "CommandCenterServiceRegistration.cs"),
            Path.Combine("src", "ArgusEngine.Gatekeeper", "Program.cs"),
            Path.Combine("src", "ArgusEngine.Workers.Spider", "Program.cs"),
            Path.Combine("src", "ArgusEngine.Workers.Enumeration", "Program.cs"),
            Path.Combine("src", "ArgusEngine.Workers.PortScan", "Program.cs"),
            Path.Combine("src", "ArgusEngine.Workers.HighValue", "Program.cs"),
            Path.Combine("src", "ArgusEngine.Workers.TechnologyIdentification", "Program.cs")
        })
        {
            var full = Path.Combine(root, path);
            Assert.True(File.Exists(full), $"{path} is missing.");
            Assert.Contains("AddArgusObservability", File.ReadAllText(full));
        }
    }

    [Fact]
    public void RepoWideRenameMigrationScriptCoversRemainingDestructiveRenames()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "apply-original-checklist-refactor.py"));

        foreach (var expected in new[]
        {
            "ArgusEngine.Application",
            "ArgusEngine.Application",
            "ArgusDbContext",
            "ArgusDbContext",
            "ArgusRuntimeOptions",
            "ArgusRuntimeOptions",
            "ArgusEngine.slnx",
            "ArgusEngine.slnx",
            "nightmare_v2"
        })
        {
            Assert.Contains(expected, script);
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.targets"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
