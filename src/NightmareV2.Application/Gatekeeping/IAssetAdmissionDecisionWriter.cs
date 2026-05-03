namespace NightmareV2.Application.Gatekeeping;

public interface IAssetAdmissionDecisionWriter
{
    Task WriteAsync(AssetAdmissionDecisionInput input, CancellationToken ct = default);
}
