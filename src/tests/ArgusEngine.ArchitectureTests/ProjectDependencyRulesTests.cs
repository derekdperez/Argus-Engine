using System.Xml.Linq;

namespace ArgusEngine.ArchitectureTests;

public sealed class ProjectDependencyRulesTests
{
    private static readonly string[] WorkerProjects =
    [
        "ArgusEngine.Workers.Enumeration",
        "ArgusEngine.Workers.HighValue",
        "ArgusEngine.Workers.HttpRequester",
        "ArgusEngine.Workers.PortScan",
        "ArgusEngine.Workers.Spider",
        "ArgusEngine.Workers.TechnologyIdentification",
    ];

    [Fact]
    public void Contracts_projects_do_not_reference_runtime_projects()
    {
        var graph = ProjectGraph.Load();

        Assert.Empty(graph.ProjectReferences("ArgusEngine.Contracts"));
        Assert.Empty(graph.ProjectReferences("ArgusEngine.CommandCenter.Contracts"));
    }

    [Fact]
    public void Domain_references_only_contracts()
    {
        var graph = ProjectGraph.Load();

        Assert.Equal(["ArgusEngine.Contracts"], graph.ProjectReferences("ArgusEngine.Domain"));
    }

    [Fact]
    public void New_gateway_does_not_reference_domain_infrastructure_or_worker_projects()
    {
        var graph = ProjectGraph.Load();
        var forbiddenPrefixes = new[]
        {
            "ArgusEngine.Application",
            "ArgusEngine.Domain",
            "ArgusEngine.Infrastructure",
            "ArgusEngine.Workers.",
            "ArgusEngine.Harness",
            "ArgusEngine.Gatekeeper",
        };

        var violations = graph.ProjectReferences("ArgusEngine.CommandCenter.Gateway")
            .Where(reference => forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void New_web_shell_does_not_reference_domain_infrastructure_or_worker_projects()
    {
        var graph = ProjectGraph.Load();
        var forbiddenPrefixes = new[]
        {
            "ArgusEngine.Application",
            "ArgusEngine.Domain",
            "ArgusEngine.Infrastructure",
            "ArgusEngine.Workers.",
            "ArgusEngine.Harness",
            "ArgusEngine.Gatekeeper",
        };

        var violations = graph.ProjectReferences("ArgusEngine.CommandCenter.Web")
            .Where(reference => forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Operations_api_does_not_reference_legacy_command_center_or_worker_projects()
    {
        var graph = ProjectGraph.Load();
        var forbiddenPrefixes = new[]
        {
            "ArgusEngine.CommandCenter",
            "ArgusEngine.Workers.",
            "ArgusEngine.Harness",
            "ArgusEngine.Gatekeeper",
        };

        var violations = graph.ProjectReferences("ArgusEngine.CommandCenter.Operations.Api")
            .Where(reference => reference != "ArgusEngine.CommandCenter.Contracts")
            .Where(reference => forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Split_command_center_hosts_do_not_reference_legacy_command_center_project()
    {
        var graph = ProjectGraph.Load();
        var splitHosts = new[]
        {
            "ArgusEngine.CommandCenter.Bootstrapper",
            "ArgusEngine.CommandCenter.Discovery.Api",
            "ArgusEngine.CommandCenter.Gateway",
            "ArgusEngine.CommandCenter.Maintenance.Api",
            "ArgusEngine.CommandCenter.Operations.Api",
            "ArgusEngine.CommandCenter.Realtime.Host",
            "ArgusEngine.CommandCenter.SpiderDispatcher",
            "ArgusEngine.CommandCenter.Updates.Api",
            "ArgusEngine.CommandCenter.Web",
            "ArgusEngine.CommandCenter.WorkerControl.Api",
        };

        var violations = splitHosts
            .SelectMany(host => graph.ProjectReferences(host).Select(reference => $"{host} -> {reference}"))
            .Where(edge => edge.EndsWith(" -> ArgusEngine.CommandCenter", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Only_spider_dispatcher_references_spider_worker_until_worker_core_split_exists()
    {
        var graph = ProjectGraph.Load();

        var violations = graph.Edges
            .Where(edge => edge.From.StartsWith("ArgusEngine.CommandCenter.", StringComparison.Ordinal))
            .Where(edge => edge.To == "ArgusEngine.Workers.Spider")
            .Where(edge => edge.From != "ArgusEngine.CommandCenter.SpiderDispatcher")
            .Select(edge => $"{edge.From} -> {edge.To}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Current_legacy_command_center_worker_references_are_documented_until_migration_finishes()
    {
        var graph = ProjectGraph.Load(includeTests: false);

        var actual = graph.Edges
            .Where(edge => edge.From == "ArgusEngine.CommandCenter" && WorkerProjects.Contains(edge.To))
            .Select(edge => edge.To)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(WorkerProjects.Order(StringComparer.Ordinal).ToArray(), actual);
    }

    [Fact(Skip = "Enable when legacy ArgusEngine.CommandCenter is no longer the runtime host.")]
    public void No_command_center_runtime_references_worker_implementation_projects()
    {
        var graph = ProjectGraph.Load(includeTests: false);

        var violations = graph.Edges
            .Where(edge => edge.From.StartsWith("ArgusEngine.CommandCenter.", StringComparison.Ordinal)
                && WorkerProjects.Contains(edge.To))
            .Select(edge => $"{edge.From} -> {edge.To}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    private sealed record ProjectEdge(string From, string To);

    private sealed class ProjectGraph
    {
        private readonly Dictionary<string, string[]> projectReferences;

        private ProjectGraph(IReadOnlyCollection<ProjectEdge> edges, Dictionary<string, string[]> projectReferences)
        {
            Edges = edges;
            this.projectReferences = projectReferences;
        }

        public IReadOnlyCollection<ProjectEdge> Edges { get; }

        public static ProjectGraph Load(bool includeTests = true)
        {
            var root = FindRepositoryRoot();
            var projectPaths = Directory
                .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => includeTests || !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .ToArray();

            var projectByPath = projectPaths.ToDictionary(
                path => Path.GetFullPath(path),
                path => Path.GetFileNameWithoutExtension(path) ?? Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase);
            var edges = new List<ProjectEdge>();
            var referencesByProject = new Dictionary<string, string[]>(StringComparer.Ordinal);

            foreach (var projectPath in projectPaths)
            {
                var projectName = Path.GetFileNameWithoutExtension(projectPath);
                var document = XDocument.Load(projectPath);
                var references = document
                    .Descendants("ProjectReference")
                    .Select(reference => reference.Attribute("Include")?.Value)
                    .OfType<string>()
                    .Where(include => !string.IsNullOrWhiteSpace(include))
                    .Select(include => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, include)))
                    .Select(path => projectByPath.TryGetValue(path, out var name)
                        ? name
                        : Path.GetFileNameWithoutExtension(path) ?? Path.GetFileName(path))
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                referencesByProject[projectName] = references;
                edges.AddRange(references.Select(reference => new ProjectEdge(projectName, reference)));
            }

            return new ProjectGraph(edges, referencesByProject);
        }

        public string[] ProjectReferences(string projectName) =>
            projectReferences.TryGetValue(projectName, out var references) ? references : [];

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
}
