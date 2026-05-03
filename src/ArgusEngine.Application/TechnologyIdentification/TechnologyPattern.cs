using System.Text.RegularExpressions;

namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record TechnologyPattern(
    string TechnologyName,
    string Source,
    string? Key,
    string RawPattern,
    Regex Regex,
    int Confidence,
    string? VersionExpression);
