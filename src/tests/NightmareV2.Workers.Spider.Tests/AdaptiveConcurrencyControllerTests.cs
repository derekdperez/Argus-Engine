using NightmareV2.Workers.Spider;
using Xunit;

namespace NightmareV2.Workers.Spider.Tests;

public sealed class AdaptiveConcurrencyControllerTests
{
    [Fact]
    public void ResolveEffectiveConcurrency_ClampsConfiguredMaxButDoesNotAdaptWithSmallSample()
    {
        var controller = new AdaptiveConcurrencyController(windowSize: 1);

        Assert.Equal(1, controller.ResolveEffectiveConcurrency(0));
        Assert.Equal(1000, controller.ResolveEffectiveConcurrency(10_000));
    }

    [Fact]
    public void ResolveEffectiveConcurrency_DropsToOneWhenFailureRateIsVeryHigh()
    {
        var controller = new AdaptiveConcurrencyController();
        Report(controller, successes: 7, failures: 13);

        Assert.Equal(1, controller.ResolveEffectiveConcurrency(20));
    }

    [Fact]
    public void ResolveEffectiveConcurrency_HalvesConfiguredMaxWhenFailureRateIsElevated()
    {
        var controller = new AdaptiveConcurrencyController();
        Report(controller, successes: 12, failures: 8);

        Assert.Equal(10, controller.ResolveEffectiveConcurrency(20));
    }

    [Fact]
    public void ResolveEffectiveConcurrency_ReducesByTwentyPercentWhenFailureRateIsModerate()
    {
        var controller = new AdaptiveConcurrencyController();
        Report(controller, successes: 16, failures: 4);

        Assert.Equal(16, controller.ResolveEffectiveConcurrency(20));
    }

    [Fact]
    public void ResolveEffectiveConcurrency_IncreasesWhenLargeRecentWindowIsHealthy()
    {
        var controller = new AdaptiveConcurrencyController();
        Report(controller, successes: 100, failures: 0);

        Assert.Equal(23, controller.ResolveEffectiveConcurrency(20));
    }

    [Fact]
    public void ResolveEffectiveConcurrency_UsesSlidingWindowForRecentOutcomes()
    {
        var controller = new AdaptiveConcurrencyController(windowSize: 50);
        Report(controller, successes: 0, failures: 60);
        Report(controller, successes: 50, failures: 0);

        Assert.Equal(20, controller.ResolveEffectiveConcurrency(20));
    }

    private static void Report(AdaptiveConcurrencyController controller, int successes, int failures)
    {
        for (var i = 0; i < successes; i++)
            controller.ReportResult(true);
        for (var i = 0; i < failures; i++)
            controller.ReportResult(false);
    }
}
