namespace ArgusEngine.Workers.HighValue;

public sealed class HighValueWordlistBootstrap(IReadOnlyList<(string Category, IReadOnlyList<string> Lines)> categories)
{
    public IReadOnlyList<(string Category, IReadOnlyList<string> Lines)> Categories { get; } = categories;
}
