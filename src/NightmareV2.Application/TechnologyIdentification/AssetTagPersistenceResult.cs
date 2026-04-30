namespace NightmareV2.Application.TechnologyIdentification;

public sealed record AssetTagPersistenceResult(
    int TechnologyCount,
    int EvidenceCount,
    int TagsAttached);
