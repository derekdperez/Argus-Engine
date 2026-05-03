using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace ArgusEngine.Infrastructure.Caching;

public class DistributedScanLock
{
    private readonly IConnectionMultiplexer _redis;

    public DistributedScanLock(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<bool> AcquireScanLockAsync(string assetKey, TimeSpan ttl)
    {
        var db = _redis.GetDatabase();

        var script = @"
        local exists = redis.call('EXISTS', KEYS[1])
        if exists == 0 then
            redis.call('SET', KEYS[1], 'Processing')
            redis.call('EXPIRE', KEYS[1], ARGV[1])
            return 1
        else
            return 0
        end";

        var result = await db.ScriptEvaluateAsync(
            LuaScript.Prepare(script), 
            new { key = (RedisKey)assetKey, ttl = (int)ttl.TotalSeconds }
        );

        return (int)result == 1;
    }
}
