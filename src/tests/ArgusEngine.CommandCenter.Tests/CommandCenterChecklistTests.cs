namespace ArgusEngine.CommandCenter.Tests;

public sealed class CommandCenterChecklistTests
{
    [Fact]
    public void ProgramStaysCompositionOnlyAndBelowSeventyFiveLines()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Program.cs");
        var text = File.ReadAllText(path);
        var lines = File.ReadAllLines(path).Count(line => !string.IsNullOrWhiteSpace(line));

        Assert.True(lines < 75, $"Program.cs should be under 75 non-empty lines; actual {lines}.");
        Assert.Contains("AddCommandCenterServices", text);
        Assert.Contains("UseCommandCenterMiddleware", text);
        Assert.Contains("MapCommandCenterEndpoints", text);
        Assert.DoesNotContain("MapPost(\"/api/targets\"", text);
        Assert.DoesNotContain("AmazonECSClient", text);
    }

    [Fact]
    public void EndpointRegistrationMapsAuditRetentionAndArtifactMaintenanceEndpoints()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "ArgusEngine.CommandCenter", "Endpoints", "CommandCenterEndpointRegistration.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("MapAssetAdmissionDecisionEndpoints", text);
        Assert.Contains("MapDataRetentionAdminEndpoints", text);
        Assert.Contains("MapHttpArtifactBackfillEndpoints", text);
        Assert.Contains("MapHub<DiscoveryHub>", text);
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
