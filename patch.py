import os
import re

def patch_files():
    # Define the project root-relative paths
    files_to_patch = {
        "src/NightmareV2.Application/Workers/IHttpRequestQueueStateMachine.cs": {
            "search": "HttpRequestQueueStateKind to",
            "replace": "HttpRequestQueueStateKind toKind"
        },
        "src/NightmareV2.Infrastructure/Messaging/BusJournalObservers.cs": {
            "transform": patch_bus_observers
        },
        "src/NightmareV2.CommandCenter/WorkerActivityQuery.cs": {
            "prepend_usings": ["using NightmareV2.Contracts;", "using NightmareV2.CommandCenter.Models;"]
        },
        "src/NightmareV2.CommandCenter/DockerRuntimeStatusBuilder.cs": {
            "prepend_usings": ["using NightmareV2.Contracts;", "using NightmareV2.CommandCenter.Models;"]
        },
        "src/NightmareV2.CommandCenter/Components/Pages/Status.razor": {
            "prepend_usings": ["@using NightmareV2.Contracts", "@using NightmareV2.CommandCenter.Models"]
        },
        "src/NightmareV2.CommandCenter/Components/Pages/HighValueFindings.razor": {
            "prepend_usings": ["@using NightmareV2.Contracts", "@using NightmareV2.CommandCenter.Models"]
        },
        "src/NightmareV2.CommandCenter/Components/Pages/OpsRadzen.razor": {
            "transform": patch_ops_radzen
        }
    }

    for path, patch in files_to_patch.items():
        if not os.path.exists(path):
            print(f"[!] Warning: File not found - {path}")
            continue

        with open(path, "r", encoding="utf-8") as f:
            content = f.read()

        new_content = content

        # Apply specific search/replace
        if "search" in patch:
            new_content = new_content.replace(patch["search"], patch["replace"])

        # Apply using/namespace prepends
        if "prepend_usings" in patch:
            usings = "\n".join(patch["prepend_usings"]) + "\n"
            if usings not in new_content:
                new_content = usings + new_content

        # Apply custom logic transformations
        if "transform" in patch:
            new_content = patch["transform"](new_content)

        if content != new_content:
            with open(path, "w", encoding="utf-8") as f:
                f.write(new_content)
            print(f"[✓] Patched: {path}")
        else:
            print(f"[~] No changes needed: {path}")

def patch_bus_observers(content):
    """Adds static keyword to methods that don't use instance data."""
    methods = ["ConsumeFault", "PostConsume", "PreConsume"]
    for method in methods:
        pattern = rf"public Task {method}<T>"
        content = re.sub(pattern, f"public static Task {method}<T>", content)
    return content

def patch_ops_radzen(content):
    """Removes duplicate Http injections and OnInitialized methods."""
    # Add missing namespaces
    usings = "@using NightmareV2.Contracts\n@using NightmareV2.CommandCenter.Models\n"
    if "@using NightmareV2.Contracts" not in content:
        content = usings + content

    # Remove duplicate @inject HttpClient Http (keeps only the first occurrence)
    inject_pattern = r"@inject HttpClient Http"
    matches = list(re.finditer(inject_pattern, content))
    if len(matches) > 1:
        # Keep the first, remove the others
        for match in reversed(matches[1:]):
            content = content[:match.start()] + content[match.end():]

    # Remove duplicate OnInitializedAsync blocks (extremely simplified check)
    # This logic assumes the second one starts later in the file
    init_pattern = r"protected override async Task OnInitializedAsync\(\).*?\{.*?\}"
    matches = list(re.finditer(init_pattern, content, re.DOTALL))
    if len(matches) > 1:
        for match in reversed(matches[1:]):
            content = content[:match.start()] + content[match.end():]

    return content

if __name__ == "__main__":
    print("Starting build error patching script...")
    patch_files()
    print("Patching complete.")