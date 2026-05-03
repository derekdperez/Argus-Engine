namespace ArgusEngine.Application.HighValue;

public sealed record HighValueFindingInput(
    Guid TargetId,
    Guid? SourceAssetId,
    string FindingType,
    string Severity,
    string PatternName,
    string? Category,
    string? MatchedText,
    string SourceUrl,
    string WorkerName,
    int? ImportanceScore);
