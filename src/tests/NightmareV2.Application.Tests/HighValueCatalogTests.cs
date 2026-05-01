using System.Text.Json;
using NightmareV2.Application.HighValue;
using Xunit;

namespace NightmareV2.Application.Tests;

public sealed class HighValueCatalogTests
{
    [Fact]
    public void PatternCatalog_LoadFromFile_FiltersIncompleteRowsAndNormalizesScope()
    {
        using var fixture = new TempDirectoryFixture();
        var path = Path.Combine(fixture.Path, "patterns.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new object[]
                {
                    new { name = "AWS", scope = " FILE_CONTENTS ", regex = "AKIA", description = "key", outputFilename = "aws.txt", importanceScore = 10 },
                    new { name = "", scope = "url", regex = "admin" },
                    new { name = "MissingRegex", scope = "url" },
                }));

        var rules = HighValuePatternCatalog.LoadFromFile(path);

        var rule = Assert.Single(rules);
        Assert.Equal("AWS", rule.Name);
        Assert.Equal("file_contents", rule.Scope);
        Assert.Equal("AKIA", rule.Pattern);
        Assert.Equal(10, rule.ImportanceScore);
    }

    [Fact]
    public void WordlistCatalog_LoadFromDirectory_SortsFilesAndSkipsCommentsEmptyFiles()
    {
        using var fixture = new TempDirectoryFixture();
        File.WriteAllLines(Path.Combine(fixture.Path, "z-last.txt"), ["# comment", " /admin ", "", "/debug"]);
        File.WriteAllLines(Path.Combine(fixture.Path, "a-first.txt"), ["# only comments"]);
        File.WriteAllLines(Path.Combine(fixture.Path, "m-middle.txt"), ["/metrics"]);

        var categories = HighValueWordlistCatalog.LoadFromDirectory(fixture.Path);

        Assert.Collection(
            categories,
            first =>
            {
                Assert.Equal("m-middle", first.Category);
                Assert.Equal(["/metrics"], first.Lines);
            },
            second =>
            {
                Assert.Equal("z-last", second.Category);
                Assert.Equal(["/admin", "/debug"], second.Lines);
            });
    }

    private sealed class TempDirectoryFixture : IDisposable
    {
        public TempDirectoryFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nightmare-app-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
