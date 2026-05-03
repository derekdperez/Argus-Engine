namespace ArgusEngine.Application.Gatekeeping;

public interface IAssetAdmissionDecisionWriter
{
    Task WriteAsync(AssetAdmissionDecisionInput input, CancellationToken ct = default);
}
