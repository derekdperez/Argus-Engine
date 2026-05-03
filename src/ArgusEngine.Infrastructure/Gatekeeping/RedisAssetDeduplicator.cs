using System.Text.Json;
using ArgusEngine.Application.Gatekeeping;
using StackExchange.Redis;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class RedisAssetDeduplicator(IConnectionMultiplexer redis) : IAssetDeduplicator
{
    private const string KeyPrefix = "argus:dedupe:";

    public async Task<bool> TryReserveAsync(Guid targetId, string canonicalKey, CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();
        var key = KeyPrefix + targetId + ":" + JsonSerializer.Serialize(canonicalKey);
        return await db.StringSetAsync(key, "1", when: When.NotExists).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(Guid targetId, string canonicalKey, CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();
        var key = KeyPrefix + targetId + ":" + JsonSerializer.Serialize(canonicalKey);
        await db.KeyDeleteAsync(key).ConfigureAwait(false);
    }

    public Task ClearForTargetAsync(Guid targetId, CancellationToken cancellationToken = default) =>
        DeleteKeysMatchingAsync(KeyPrefix + targetId + ":*", cancellationToken);

    public Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        DeleteKeysMatchingAsync(KeyPrefix + "*", cancellationToken);

    private async Task DeleteKeysMatchingAsync(string pattern, CancellationToken cancellationToken)
    {
        await Task.Run(
                () =>
                {
                    var db = redis.GetDatabase();
                    foreach (var endpoint in redis.GetEndPoints())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var server = redis.GetServer(endpoint);
                        if (!server.IsConnected || server.IsReplica)
                            continue;
                        foreach (var key in server.Keys(database: db.Database, pattern: pattern))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            db.KeyDelete(key);
                        }
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
