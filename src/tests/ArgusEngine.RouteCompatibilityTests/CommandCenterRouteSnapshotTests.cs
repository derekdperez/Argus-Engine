using System.Text.RegularExpressions;

namespace ArgusEngine.RouteCompatibilityTests;

public sealed class CommandCenterRouteSnapshotTests
{
    [Fact]
    public void Legacy_command_center_route_surface_matches_snapshot()
    {
        var root = FindRepositoryRoot();
        var expectedPath = Path.Combine(
            root,
            "src",
            "tests",
            "ArgusEngine.RouteCompatibilityTests",
            "Snapshots",
            "commandcenter-routes.txt");

        var expected = File.ReadAllLines(expectedPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = ExtractRoutes(root);

        Assert.Equal(expected, actual);
    }

    private static string[] ExtractRoutes(string root)
    {
        var commandCenterRoot = Path.Combine(root, "src", "ArgusEngine.CommandCenter");
        var routes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(commandCenterRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var groups = Regex.Matches(text, @"var\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*[A-Za-z_][A-Za-z0-9_]*\.MapGroup\s*\(\s*""(?<route>[^""]+)""")
                .ToDictionary(
                    match => match.Groups["name"].Value,
                    match => match.Groups["route"].Value,
                    StringComparer.Ordinal);

            foreach (Match match in Regex.Matches(text, @"(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\.Map(?<method>Get|Post|Put|Delete|Patch)\s*\(\s*""(?<route>[^""]*)"""))
            {
                var prefix = groups.GetValueOrDefault(match.Groups["receiver"].Value, string.Empty);
                routes.Add($"{match.Groups["method"].Value.ToUpperInvariant()} {NormalizeRoute(prefix, match.Groups["route"].Value)} [{relativePath}]");
            }

            foreach (Match match in Regex.Matches(text, @"MapHub<(?<hub>[^>]+)>\s*\(\s*""(?<route>[^""]+)"""))
            {
                routes.Add($"HUB {match.Groups["route"].Value} [{relativePath}]");
            }

            foreach (Match match in Regex.Matches(text, @"@page\s+""(?<route>[^""]+)"""))
            {
                routes.Add($"PAGE {match.Groups["route"].Value} [{relativePath}]");
            }
        }

        return routes.ToArray();
    }

    private static string NormalizeRoute(string prefix, string route)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return route;
        }

        if (route == "/")
        {
            return prefix;
        }

        return route.Length > 0 && route[0] == '/' ? prefix + route : $"{prefix}/{route}";
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
