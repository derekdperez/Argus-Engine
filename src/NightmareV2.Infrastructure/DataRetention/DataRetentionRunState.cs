using NightmareV2.Application.DataRetention;

namespace NightmareV2.Infrastructure.DataRetention;

public sealed class DataRetentionRunState
{
    private readonly object _sync = new();

    public DateTimeOffset? LastRunAtUtc { get; private set; }

    public DataRetentionRunResult? LastResult { get; private set; }

    public void Record(DataRetentionRunResult result)
    {
        lock (_sync)
        {
            LastRunAtUtc = DateTimeOffset.UtcNow;
            LastResult = result;
        }
    }
}
