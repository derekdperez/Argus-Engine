namespace ArgusEngine.Domain.Entities;

public static class AssetLifecycleStatus
{
    /// <summary>Accepted and waiting to be requested/probed.</summary>
    public const string Queued = "Queued";
    public const string Confirmed = "Confirmed";
    /// <summary>Requested but did not exist / fetch failed (e.g. 404 or non-2xx).</summary>
    public const string NonExistent = "NonExistent";
}
