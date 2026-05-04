using ArgusEngine.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class ArgusConfigurationCompatibilityTests
{
    [Fact]
    public void GetArgusValue_prefers_current_argus_key_over_legacy_nightmare_key()
    {
        var configuration = Build(
            new("Argus:WorkerScaling:MaxDesiredCount", "7"),
            new("Nightmare:WorkerScaling:MaxDesiredCount", "3"));

        var value = configuration.GetArgusValue<int>("WorkerScaling:MaxDesiredCount", 0);

        Assert.Equal(7, value);
    }

    [Fact]
    public void GetArgusValue_falls_back_to_legacy_nightmare_key()
    {
        var configuration = Build(new("Nightmare:WorkerScaling:MaxDesiredCount", "3"));

        var value = configuration.GetArgusValue<int>("WorkerScaling:MaxDesiredCount", 0);

        Assert.Equal(3, value);
    }

    [Fact]
    public void GetArgusValue_reads_current_screaming_snake_key()
    {
        var configuration = Build(new("ARGUS_QUEUE_HTTP_DEPTH", "42"));

        var value = configuration.GetArgusValue<int>("Queue:HttpDepth", 0);

        Assert.Equal(42, value);
    }

    [Fact]
    public void GetArgusValue_reads_legacy_screaming_snake_key()
    {
        var configuration = Build(new("NIGHTMARE_QUEUE_HTTP_DEPTH", "41"));

        var value = configuration.GetArgusValue<int>("Queue:HttpDepth", 0);

        Assert.Equal(41, value);
    }

    [Fact]
    public void GetArgusValue_converts_booleans_and_numbers()
    {
        var configuration = Build(
            new("Argus:Feature:Enabled", "true"),
            new("Argus:Feature:MaxItems", "12"));

        Assert.True(configuration.GetArgusValue("Feature:Enabled", false));
        Assert.Equal(12, configuration.GetArgusValue("Feature:MaxItems", 0));
    }

    [Fact]
    public void GetArgusCompatibilityKeys_returns_current_and_legacy_keys()
    {
        var keys = ArgusConfiguration.GetArgusCompatibilityKeys("WorkerScaling:MaxDesiredCount");

        Assert.Contains("Argus:WorkerScaling:MaxDesiredCount", keys);
        Assert.Contains("Nightmare:WorkerScaling:MaxDesiredCount", keys);
        Assert.Contains("ARGUS_WORKER_SCALING_MAX_DESIRED_COUNT", keys);
        Assert.Contains("NIGHTMARE_WORKER_SCALING_MAX_DESIRED_COUNT", keys);
    }

    private static IConfiguration Build(params KeyValuePair<string, string?>[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
