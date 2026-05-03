namespace ArgusEngine.Application.TechnologyIdentification;

public interface IAssetTagService
{
    Task SeedTechnologyTagsAsync(
        IReadOnlyCollection<TechnologyDefinition> technologies,
        CancellationToken cancellationToken = default);

    Task<AssetTagPersistenceResult> UpsertTechnologyDetectionsAsync(
        Guid targetId,
        Guid assetId,
        IReadOnlyCollection<TechnologyScanResult> results,
        IReadOnlyDictionary<string, TechnologyDefinition> definitions,
        CancellationToken cancellationToken = default);
}
