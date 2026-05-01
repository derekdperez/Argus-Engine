using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Workers;
using Xunit;

namespace NightmareV2.Infrastructure.Tests;

public sealed class HttpRequestQueueStateMachineTests
{
    private readonly DefaultHttpRequestQueueStateMachine _stateMachine = new();

    [Theory]
    [InlineData(HttpRequestQueueStateKind.Queued, HttpRequestQueueStateKind.InFlight)]
    [InlineData(HttpRequestQueueStateKind.Queued, HttpRequestQueueStateKind.Failed)]
    [InlineData(HttpRequestQueueStateKind.InFlight, HttpRequestQueueStateKind.Succeeded)]
    [InlineData(HttpRequestQueueStateKind.InFlight, HttpRequestQueueStateKind.Retry)]
    [InlineData(HttpRequestQueueStateKind.InFlight, HttpRequestQueueStateKind.Failed)]
    [InlineData(HttpRequestQueueStateKind.Retry, HttpRequestQueueStateKind.InFlight)]
    [InlineData(HttpRequestQueueStateKind.Retry, HttpRequestQueueStateKind.Failed)]
    public void CanTransition_AllowsExpectedWorkflowEdges(HttpRequestQueueStateKind from, HttpRequestQueueStateKind to)
    {
        Assert.True(_stateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(HttpRequestQueueStateKind.Succeeded, HttpRequestQueueStateKind.Retry)]
    [InlineData(HttpRequestQueueStateKind.Succeeded, HttpRequestQueueStateKind.Failed)]
    [InlineData(HttpRequestQueueStateKind.Failed, HttpRequestQueueStateKind.Queued)]
    [InlineData(HttpRequestQueueStateKind.Queued, HttpRequestQueueStateKind.Succeeded)]
    [InlineData(HttpRequestQueueStateKind.Retry, HttpRequestQueueStateKind.Succeeded)]
    public void CanTransition_RejectsTerminalOrSkippedTransitions(HttpRequestQueueStateKind from, HttpRequestQueueStateKind to)
    {
        Assert.False(_stateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(HttpRequestQueueStateKind.Queued)]
    [InlineData(HttpRequestQueueStateKind.InFlight)]
    [InlineData(HttpRequestQueueStateKind.Succeeded)]
    [InlineData(HttpRequestQueueStateKind.Retry)]
    [InlineData(HttpRequestQueueStateKind.Failed)]
    public void CanTransition_IsIdempotentForCurrentState(HttpRequestQueueStateKind state)
    {
        Assert.True(_stateMachine.CanTransition(state, state));
    }
}
