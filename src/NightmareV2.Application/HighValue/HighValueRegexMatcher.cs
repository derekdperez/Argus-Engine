using System.Linq;

namespace NightmareV2.Application.HighValue;

/// <summary>High-value policy bundle from <see cref="HighValuePatternCatalog"/>.</summary>
public sealed class HighValueRegexMatcher : RegexMatchingWorkerBase
{
    private readonly CompiledRegexRule[] _rules;

    public HighValueRegexMatcher(IReadOnlyList<RegexRuleDefinition> definitions) =>
        _rules = CompileDefinitions(definitions).ToArray();

    protected override IReadOnlyList<CompiledRegexRule> Rules => _rules;
}
