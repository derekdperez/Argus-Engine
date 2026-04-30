import os
import re

def patch_project():
    # 1. Fix BusJournalObservers.cs (CS0736)
    # Reverting static methods to instance methods to satisfy IConsumeObserver
    bus_observers_path = "src/NightmareV2.Infrastructure/Messaging/BusJournalObservers.cs"
    if os.path.exists(bus_observers_path):
        with open(bus_observers_path, "r", encoding="utf-8") as f:
            content = f.read()
        
        # Remove the 'static' keyword that caused the interface implementation failure
        new_content = content.replace("public static Task ConsumeFault", "public Task ConsumeFault")
        new_content = new_content.replace("public static Task PostConsume", "public Task PostConsume")
        new_content = new_content.replace("public static Task PreConsume", "public Task PreConsume")
        
        with open(bus_observers_path, "w", encoding="utf-8") as f:
            f.write(new_content)
        print(f"[✓] Fixed CS0736 in {bus_observers_path}")

    # 2. Fix GatekeeperOrchestrator.cs (CA1848, CA1873)
    # Implementing LoggerMessage source generation for high-performance logging
    gatekeeper_path = "src/NightmareV2.Application/Gatekeeping/GatekeeperOrchestrator.cs"
    if os.path.exists(gatekeeper_path):
        with open(gatekeeper_path, "r", encoding="utf-8") as f:
            lines = f.readlines()

        new_lines = []
        class_definition_found = False
        
        for line in lines:
            # CA1848/CA1873 require the class to be 'partial' for source generators
            if "public class GatekeeperOrchestrator" in line and "partial" not in line:
                line = line.replace("public class", "public partial class")
                class_definition_found = True
            
            # Replace inline Log calls with high-performance partial method calls
            line = line.replace('_logger.LogDebug("Processing asset: {Asset}", asset.CanonicalUrl);', 
                                'LogProcessingAsset(_logger, asset.CanonicalUrl);')
            line = line.replace('_logger.LogDebug("Asset is in scope: {Asset}", asset.CanonicalUrl);', 
                                'LogAssetInScope(_logger, asset.CanonicalUrl);')
            line = line.replace('_logger.LogDebug("Asset is not in scope: {Asset}", asset.CanonicalUrl);', 
                                'LogAssetOutOfScope(_logger, asset.CanonicalUrl);')
            line = line.replace('_logger.LogInformation("Asset admitted to stage {Stage}: {Asset}", stage, asset.CanonicalUrl);', 
                                'LogAssetAdmitted(_logger, stage.ToString(), asset.CanonicalUrl);')
            
            new_lines.append(line)

        # Append the LoggerMessage definitions at the end of the class
        # We find the last closing brace and insert before it
        if class_definition_found:
            for i in range(len(new_lines) - 1, -1, -1):
                if "}" in new_lines[i]:
                    logger_defs = """
    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing asset: {Asset}")]
    static partial void LogProcessingAsset(ILogger logger, string asset);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asset is in scope: {Asset}")]
    static partial void LogAssetInScope(ILogger logger, string asset);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asset is not in scope: {Asset}")]
    static partial void LogAssetOutOfScope(ILogger logger, string asset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asset admitted to stage {Stage}: {Asset}")]
    static partial void LogAssetAdmitted(ILogger logger, string stage, string asset);
"""
                    new_lines.insert(i, logger_defs)
                    break

        with open(gatekeeper_path, "w", encoding="utf-8") as f:
            f.writelines(new_lines)
        print(f"[✓] Fixed CA1848/CA1873 in {gatekeeper_path}")

if __name__ == "__main__":
    patch_project()