using System.Diagnostics;

namespace ArgusEngine.Infrastructure.Observability;

public sealed class ArgusTracing
{
    public const string ActivitySourceName = "ArgusEngine";
    public static readonly ActivitySource Source = new(ActivitySourceName);
}
