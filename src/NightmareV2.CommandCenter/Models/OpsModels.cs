namespace NightmareV2.CommandCenter.Models;

public record RestartToolRequest(string[]? TargetIds, bool AllTargets);
