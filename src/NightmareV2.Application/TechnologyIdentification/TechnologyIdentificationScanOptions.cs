namespace NightmareV2.Application.TechnologyIdentification;

public sealed class TechnologyIdentificationScanOptions
{
    public int MaxResponseBodyScanBytes { get; set; } = 500_000;
}
