using System.Threading.Tasks;
using MassTransit;
using MassTransit.Context;

namespace ArgusEngine.Infrastructure.Messaging;

public class WorkerCancellationFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    private readonly WorkerCancellationTracker _tracker;

    public WorkerCancellationFilter(WorkerCancellationTracker tracker)
    {
        _tracker = tracker;
    }

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (context.MessageId.HasValue)
        {
            using var cts = _tracker.CreateLinkedCts(context.MessageId.Value, context.CancellationToken);
            try
            {
                await next.Send(new CancellationConsumeContext(context, cts.Token)).ConfigureAwait(false);
            }
            finally
            {
                _tracker.Untrack(context.MessageId.Value);
            }
        }
        else
        {
            await next.Send(context).ConfigureAwait(false);
        }
    }

    private sealed class CancellationConsumeContext(ConsumeContext<T> context, CancellationToken ct) 
        : ConsumeContextProxy<T>(context)
    {
        public override CancellationToken CancellationToken => ct;
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("worker-cancellation");
    }
}
