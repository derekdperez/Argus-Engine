import os
from pathlib import Path

def patch_repo():
    # Define the root of your repository (assumes script is run from repo root)
    repo_root = Path(os.getcwd())

    # 1. Patch OpsSnapshotBuilder.cs (LINQ Translation Fix)
    ops_snapshot_path = repo_root / "src/NightmareV2.CommandCenter/OpsSnapshotBuilder.cs"
    if ops_snapshot_path.exists():
        content = ops_snapshot_path.read_text()
        
        # Target the complex grouping projection
        old_linq = ".Select(g => new AssetCountByDomainDto { Domain = g.Key, Count = g.LongCount() })"
        new_linq = ".Select(g => new AssetCountByDomainDto(g.Key, g.LongCount()))"
        
        if old_linq in content:
            new_content = content.replace(old_linq, new_linq)
            ops_snapshot_path.write_text(new_content)
            print(f"Successfully patched: {ops_snapshot_path}")
        else:
            print(f"Could not find target LINQ pattern in {ops_snapshot_path}")
    else:
        print(f"File not found: {ops_snapshot_path}")

    # 2. Patch OutboxDispatcherWorker.cs (Timeout & Resiliency Fix)
    dispatcher_path = repo_root / "src/NightmareV2.Infrastructure/Messaging/OutboxDispatcherWorker.cs"
    if dispatcher_path.exists():
        content = dispatcher_path.read_text()
        
        # Add a delay to the loop to prevent tight-looping on the DB
        loop_pattern = "while (!stoppingToken.IsCancellationRequested)"
        loop_replacement = "while (!stoppingToken.IsCancellationRequested)\n            {\n                await Task.Delay(50, stoppingToken);"
        
        # Increase command timeout logic (Simplified for script)
        timeout_pattern = "await db.Database.BeginTransactionAsync(ct)"
        timeout_replacement = "db.Database.SetCommandTimeout(30);\n                await db.Database.BeginTransactionAsync(ct)"

        if loop_pattern in content and timeout_pattern in content:
            content = content.replace(loop_pattern, loop_replacement)
            content = content.replace(timeout_pattern, timeout_replacement)
            dispatcher_path.write_text(content)
            print(f"Successfully patched: {dispatcher_path}")
        else:
            print(f"Target patterns not found in {dispatcher_path}")
    else:
        print(f"File not found: {dispatcher_path}")

if __name__ == "__main__":
    patch_repo()