namespace NightmareV2.Contracts;

/// <summary>Where scannable content originated (drives which regex scopes apply).</summary>
public enum ScannableContentSource
{
    /// <summary>HTTP response body and headers stored on a URL asset (<see cref="Events.ScannableContentAvailable"/>).</summary>
    UrlHttpResponse = 0,
}
