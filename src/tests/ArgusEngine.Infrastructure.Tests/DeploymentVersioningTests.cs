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
}
