using System.Text.Json;
using MassTransit;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class BusJournalPublishObserver(BusJournalBuffer buffer) : IPublishObserver
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public Task PrePublish<T>(PublishContext<T> context)
        where T : class =>
        Task.CompletedTask;

    public Task PostPublish<T>(PublishContext<T> context)
        where T : class
    {
        buffer.TryEnqueue(
            direction: "Publish",
            messageType: typeof(T).Name,
            payloadJson: JsonSerializer.Serialize(context.Message, context.Message!.GetType(), JsonOpts),
            consumerType: null);
        return Task.CompletedTask;
    }

    public Task PublishFault<T>(PublishContext<T> context, Exception exception)
        where T : class =>
        Task.CompletedTask;
}

public sealed class BusJournalConsumeObserver(BusJournalBuffer buffer) : IConsumeObserver
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private sealed record ConsumeStartTime(DateTimeOffset Value);

    public Task PreConsume<T>(ConsumeContext<T> context)
        where T : class
    {
        var now = DateTimeOffset.UtcNow;
        context.GetOrAddPayload(() => new ConsumeStartTime(now));

        buffer.TryEnqueue(
            direction: "Consume",
            messageType: typeof(T).Name,
            payloadJson: JsonSerializer.Serialize(context.Message!, context.Message!.GetType(), JsonOpts),
            consumerType: ResolveConsumerClrName(context),
            status: "Started",
            messageId: context.MessageId);
        
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context)
        where T : class
    {
        var duration = ResolveDuration(context);
        
        buffer.TryEnqueue(
            direction: "Consume",
            messageType: typeof(T).Name,
            payloadJson: JsonSerializer.Serialize(context.Message!, context.Message!.GetType(), JsonOpts),
            consumerType: ResolveConsumerClrName(context),
            status: "Completed",
            durationMs: duration,
            messageId: context.MessageId);
        return Task.CompletedTask;
    }

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception)
        where T : class
    {
        var duration = ResolveDuration(context);

        buffer.TryEnqueue(
            direction: "Consume",
            messageType: typeof(T).Name,
            payloadJson: JsonSerializer.Serialize(context.Message!, context.Message!.GetType(), JsonOpts),
            consumerType: ResolveConsumerClrName(context),
            status: "Failed",
            durationMs: duration,
            error: exception.Message,
            messageId: context.MessageId);
        return Task.CompletedTask;
    }

    public static Task ConsumeFault(ConsumeContext context, Exception exception) => Task.CompletedTask;
    public static Task PostConsume(ConsumeContext context) => Task.CompletedTask;
    public static Task PreConsume(ConsumeContext context) => Task.CompletedTask;

    private static double? ResolveDuration(ConsumeContext context)
    {
        if (context.TryGetPayload<ConsumeStartTime>(out var startTime))
        {
            return (DateTimeOffset.UtcNow - startTime.Value).TotalMilliseconds;
        }
        return null;
    }

    private static string? ResolveConsumerClrName<T>(ConsumeContext<T> context)
        where T : class
    {
        // ... (rest of the method as is)
        foreach (var arg in context.GetType().GetGenericArguments())
        {
            if (typeof(IConsumer<T>).IsAssignableFrom(arg))
                return arg.FullName;
        }

        return typeof(T).FullName;
    }
}
