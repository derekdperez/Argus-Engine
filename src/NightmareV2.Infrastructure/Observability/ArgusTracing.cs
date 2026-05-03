using System.Diagnostics;

namespace NightmareV2.Infrastructure.Observability;

public sealed class ArgusTracing
{
    public const string ActivitySourceName = "ArgusEngine";
    public static readonly ActivitySource Source = new(ActivitySourceName);
}
