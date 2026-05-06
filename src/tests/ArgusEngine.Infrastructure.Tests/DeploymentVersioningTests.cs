using System.Text.RegularExpressions;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class DeploymentVersioningTests
{
    private const string ExpectedVersion = "2.6.1";
    private const string ExpectedFileVersion = "2.6.1.0";

    [Fact]
    public void CentralBuildTargetsForceTheDeploymentVersionForAllProjects()
    {
        var text = File.ReadAllText(ProjectRoot("Directory.Build.targets"));

        Assert.Contains($"<ArgusEngineDeploymentVersion>{ExpectedVersion}</ArgusEngineDeploymentVersion>", text);
        Assert.Contains($"<Version>$(ArgusEngineDeploymentVersion)</Version>", text);
        Assert.Contains($"<AssemblyVersion>{ExpectedFileVersion}</AssemblyVersion>", text);
        Assert.Contains($"<FileVersion>{ExpectedFileVersion}</FileVersion>", text);
        Assert.Contains($"<InformationalVersion>$(ArgusEngineDeploymentVersion)</InformationalVersion>", text);
    }

    [Theory]
    [InlineData("deploy/Dockerfile.web")]
    [InlineData("deploy/Dockerfile.worker")]
    [InlineData("deploy/Dockerfile.worker-enum")]
    public void DockerfilesDefaultToTheCurrentDeploymentVersion(string relativePath)
    {
        var text = File.ReadAllText(ProjectRoot(relativePath));

        Assert.Contains($"ARG COMPONENT_VERSION={ExpectedVersion}", text);
        Assert.Contains("org.opencontainers.image.version", text);
        Assert.Contains("ARGUS_COMPONENT_VERSION", text);
        Assert.DoesNotContain("ARG COMPONENT_VERSION=2.0.0", text);
    }

    [Fact]
    public void ComposeTagsAndBuildArgsUseTheCurrentDeploymentVersion()
    {
        var text = File.ReadAllText(ProjectRoot("deploy/docker-compose.yml"));

        Assert.Contains($"ARGUS_ENGINE_VERSION:-{ExpectedVersion}", text);
        Assert.Contains("argus-engine/command-center", text);
        Assert.Contains("argus-engine/gatekeeper", text);
        Assert.Contains("argus-engine/worker-spider", text);
        Assert.DoesNotMatch(new Regex(@"VERSION_[A-Z_]+:-2\.0\.0"), text);
        Assert.DoesNotContain("nightmare-v2/", text);
    }

    [Theory]
    [InlineData("gatekeeper")]
    [InlineData("worker-spider")]
    [InlineData("worker-enum")]
    [InlineData("worker-portscan")]
    [InlineData("worker-highvalue")]
    [InlineData("worker-techid")]
    public void SkippedBootstrapWorkersWaitForCommandCenterSchemaAndRestartAfterTransientBootFailure(string service)
    {
        var text = File.ReadAllText(ProjectRoot("deploy/docker-compose.yml"));
        var block = ServiceBlock(text, service);

        Assert.Contains("restart: unless-stopped", block);
        Assert.Contains("command-center:", block);
        Assert.Contains("condition: service_healthy", block);
    }

    [Fact]
    public void DeployHelperUsesCurrentEnumerationWorkerProjectName()
    {
        var text = File.ReadAllText(ProjectRoot("deploy/lib-argus-compose.sh"));

        Assert.Contains("src/ArgusEngine.Workers.Enumeration", text);
        Assert.Contains("ArgusEngine.Workers.Enumeration.dll", text);
        Assert.DoesNotContain("\"src/ArgusEngine.Workers.Enum\"", text);
        Assert.DoesNotContain("ArgusEngine.Workers.Enum.dll", text);
    }

    [Fact]
    public void SpiderWorkerTreatsQueuedRowsWithoutNextAttemptAsReady()
    {
        var text = File.ReadAllText(ProjectRoot("src/ArgusEngine.Workers.Spider/HttpRequestQueueWorker.cs"));

        Assert.Contains("next_attempt_at_utc IS NULL OR next_attempt_at_utc <=", text);
    }

    [Theory]
    [InlineData("src/ArgusEngine.Gatekeeper/Program.cs")]
    [InlineData("src/ArgusEngine.Workers.Spider/Program.cs")]
    [InlineData("src/ArgusEngine.Workers.Enumeration/Program.cs")]
    [InlineData("src/ArgusEngine.Workers.PortScan/Program.cs")]
    [InlineData("src/ArgusEngine.Workers.HighValue/Program.cs")]
    [InlineData("src/ArgusEngine.Workers.TechnologyIdentification/Program.cs")]
    public void NonCommandCenterProcessesDoNotStartOutboxDispatchers(string relativePath)
    {
        var text = File.ReadAllText(ProjectRoot(relativePath));

        Assert.Contains("enableOutboxDispatcher: false", text);
    }

    [Fact]
    public void CommandCenterKeepsTheSingleOutboxDispatcher()
    {
        var commandCenter = File.ReadAllText(ProjectRoot("src/ArgusEngine.CommandCenter/Startup/CommandCenterServiceRegistration.cs"));
        var infrastructure = File.ReadAllText(ProjectRoot("src/ArgusEngine.Infrastructure/DependencyInjection.cs"));

        Assert.Contains("services.AddArgusInfrastructure(configuration);", commandCenter);
        Assert.Contains("bool enableOutboxDispatcher = true", infrastructure);
        Assert.Contains("if (enableOutboxDispatcher)", infrastructure);
        Assert.Contains("services.AddHostedService<OutboxDispatcherWorker>();", infrastructure);
    }

    [Fact]
    public void HttpQueueDefaultsMatchOperationsThroughputDefaults()
    {
        var settings = File.ReadAllText(ProjectRoot("src/ArgusEngine.Domain/Entities/HttpRequestQueueSettings.cs"));
        var endpoints = File.ReadAllText(ProjectRoot("src/ArgusEngine.CommandCenter/Endpoints/HttpRequestQueueEndpoints.cs"));

        Assert.Contains("GlobalRequestsPerMinute { get; set; } = 120_000", settings);
        Assert.Contains("PerDomainRequestsPerMinute { get; set; } = 120", settings);
        Assert.Contains("RequestTimeoutSeconds { get; set; } = 30", settings);
        Assert.Contains("Math.Clamp(body.GlobalRequestsPerMinute, 1, 120_000)", endpoints);
    }

    private static string ProjectRoot(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.");
    }

    private static string ServiceBlock(string composeText, string service)
    {
        var pattern = $@"(?ms)^  {Regex.Escape(service)}:\r?\n(?<block>.*?)(?=^  [a-zA-Z0-9_-]+:\r?\n|^volumes:)";
        var match = Regex.Match(composeText, pattern);

        Assert.True(match.Success, $"Could not find compose service block for {service}.");

        return match.Groups["block"].Value;
    }
}
