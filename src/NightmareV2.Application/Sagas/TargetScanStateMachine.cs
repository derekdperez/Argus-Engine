using System;
using MassTransit;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Application.Sagas;

public class TargetScanState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = null!;
    public string TargetDomain { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TargetScanStateMachine : MassTransitStateMachine<TargetScanState>
{
    public State Enumerating { get; private set; } = null!;
    public State Profiling { get; private set; } = null!;
    public State Fuzzing { get; private set; } = null!;
    public State Completed { get; private set; } = null!;

    public Event<IStartScanEvent> StartScan { get; private set; } = null!;
    public Event<IEnumCompletedEvent> EnumCompleted { get; private set; } = null!;
    public Event<IProfilingCompletedEvent> ProfilingCompleted { get; private set; } = null!;
    public Event<IScanFaultedEvent> ScanFaulted { get; private set; } = null!;

    public TargetScanStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => StartScan, x => x.CorrelateBy(state => state.TargetDomain, context => context.Message.Domain).SelectId(context => Guid.NewGuid()));
        Event(() => EnumCompleted, x => x.CorrelateBy(state => state.TargetDomain, context => context.Message.Domain));
        Event(() => ProfilingCompleted, x => x.CorrelateBy(state => state.TargetDomain, context => context.Message.Domain));
        Event(() => ScanFaulted, x => x.CorrelateBy(state => state.TargetDomain, context => context.Message.Domain));

        Initially(
            When(StartScan)
                .Then(context => {
                    context.Saga.TargetDomain = context.Message.Domain;
                    context.Saga.CreatedAt = DateTime.UtcNow;
                    context.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(Enumerating)
                .Publish(context => new TriggerEnumJob { Domain = context.Message.Domain })
        );

        During(Enumerating,
            When(EnumCompleted)
                .Then(context => context.Saga.UpdatedAt = DateTime.UtcNow)
                .TransitionTo(Profiling)
                .Publish(context => new TriggerProfilingJob { Domain = context.Saga.TargetDomain })
        );

        During(Profiling,
            When(ProfilingCompleted)
                .Then(context => context.Saga.UpdatedAt = DateTime.UtcNow)
                .TransitionTo(Fuzzing)
                .Publish(context => new TriggerFuzzingJob { Domain = context.Saga.TargetDomain })
        );

        DuringAny(
            When(ScanFaulted)
                .Then(context => {
                    context.Saga.UpdatedAt = DateTime.UtcNow;
                    Console.WriteLine($"Scan for {context.Saga.TargetDomain} failed.");
                })
                .TransitionTo(Completed)
        );
    }
}
