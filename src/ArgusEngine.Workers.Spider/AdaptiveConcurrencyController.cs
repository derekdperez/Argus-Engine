namespace ArgusEngine.Workers.Spider;

public sealed class AdaptiveConcurrencyController
{
    private readonly Queue<bool> _recentOutcomes = new();
    private readonly object _sync = new();
    private readonly int _windowSize;

    public AdaptiveConcurrencyController(int windowSize = 250)
    {
        _windowSize = Math.Clamp(windowSize, 50, 2000);
    }

    public void ReportResult(bool success)
    {
        lock (_sync)
        {
            _recentOutcomes.Enqueue(success);
            while (_recentOutcomes.Count > _windowSize)
                _recentOutcomes.Dequeue();
        }
    }

    public int ResolveEffectiveConcurrency(int configuredMaxConcurrency)
    {
        var baseValue = Math.Clamp(configuredMaxConcurrency, 1, 1000);
        lock (_sync)
        {
            if (_recentOutcomes.Count < 20)
                return baseValue;

            var successCount = _recentOutcomes.Count(x => x);
            var failureRate = 1.0 - (successCount / (double)_recentOutcomes.Count);

            if (failureRate >= 0.65)
                return 1;
            if (failureRate >= 0.4)
                return Math.Max(1, baseValue / 2);
            if (failureRate >= 0.2)
                return Math.Max(1, (int)Math.Floor(baseValue * 0.8));

            if (failureRate <= 0.05 && _recentOutcomes.Count >= 100)
                return Math.Min(1000, baseValue + Math.Max(1, (int)Math.Ceiling(baseValue * 0.15)));

            return baseValue;
        }
    }
}
