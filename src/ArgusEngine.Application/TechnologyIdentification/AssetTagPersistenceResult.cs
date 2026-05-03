namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record AssetTagPersistenceResult(
    int TechnologyCount,
    int EvidenceCount,
    int TagsAttached);
