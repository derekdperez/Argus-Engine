using System.Collections.Concurrent;
using ArgusEngine.Application.Http;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Infrastructure.Http;

public sealed record HttpRateLimitOptions
{
    public int DefaultDelayMs { get; init; } = 500;
    public int MaxDelayMs { get; init; } = 5000;
}

public sealed class InMemoryHttpRateLimiter(IOptions<HttpRateLimitOptions> options) : IHttpRateLimiter
{
    private readonly ConcurrentDictionary<string, DomainState> _states = new();
    private readonly HttpRateLimitOptions _options = options.Value;

    public async Task WaitAsync(string domainKey, CancellationToken ct)
    {
        var state = _states.GetOrAdd(domainKey, _ => new DomainState(_options.DefaultDelayMs));
        
        await state.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var timeSinceLast = now - state.LastRequestAt;
            var waitTime = TimeSpan.FromMilliseconds(state.CurrentDelayMs) - timeSinceLast;

            if (waitTime > TimeSpan.Zero)
            {
                await Task.Delay(waitTime, ct).ConfigureAwait(false);
            }

            state.LastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    public void RecordCompletion(string domainKey, bool success, TimeSpan duration)
    {
        if (!_states.TryGetValue(domainKey, out var state))
            return;

        if (success)
        {
            // Slowly decrease delay on success
            state.CurrentDelayMs = Math.Max(_options.DefaultDelayMs, state.CurrentDelayMs - 50);
        }
        else
        {
            // Rapidly increase delay on failure
            state.CurrentDelayMs = Math.Min(_options.MaxDelayMs, state.CurrentDelayMs + 500);
        }
    }

    private sealed class DomainState(int initialDelay)
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTimeOffset LastRequestAt { get; set; } = DateTimeOffset.MinValue;
        public int CurrentDelayMs { get; set; } = initialDelay;
    }
}
