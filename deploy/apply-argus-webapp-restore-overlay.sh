#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT"

GOOD_OPS_REF="${ARGUS_GOOD_OPS_REF:-dd5e740}"
OPS="src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor"
ADMIN="src/ArgusEngine.CommandCenter.Web/Components/Pages/Admin.razor"
ADMIN_CSS="src/ArgusEngine.CommandCenter.Web/Components/Pages/Admin.razor.css"

echo "[argus overlay] Restoring web app navigation to Ops/Admin/Development."

# Restore the Ops page version that includes the Asset Storage card and dash loading placeholders.
# This commit is from the same GitHub repo history and is the version explicitly requested.
if git cat-file -e "${GOOD_OPS_REF}^{commit}" 2>/dev/null \
    && git show "${GOOD_OPS_REF}:${OPS}" | grep -q "TotalTrackedStorageBytes"; then
    git show "${GOOD_OPS_REF}:${OPS}" > "$OPS"
else
    echo "[argus overlay] ERROR: Could not restore ${OPS} from ${GOOD_OPS_REF}." >&2
    echo "[argus overlay] Make sure the local clone has repository history, or run: git fetch --unshallow origin main" >&2
    exit 1
fi

# Restore Admin because a prior overlay replaced it with a no-route placeholder.
if git cat-file -e "${GOOD_OPS_REF}^{commit}" 2>/dev/null \
    && git show "${GOOD_OPS_REF}:${ADMIN}" | grep -q '@page "/admin"'; then
    git show "${GOOD_OPS_REF}:${ADMIN}" > "$ADMIN"
fi

if git cat-file -e "${GOOD_OPS_REF}^{commit}" 2>/dev/null \
    && git show "${GOOD_OPS_REF}:${ADMIN_CSS}" >/dev/null 2>&1; then
    git show "${GOOD_OPS_REF}:${ADMIN_CSS}" > "$ADMIN_CSS"
fi

python3 - <<'PY'
from pathlib import Path
import re

root = Path('.')
ops = root / "src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor"
text = ops.read_text(encoding="utf-8")

# Append the moved High Value and Technology ID surfaces at the bottom without changing existing Ops controls.
if "<OpsAdditionalPanels" not in text:
    marker = "@code {"
    if marker not in text:
        raise SystemExit("OpsRadzen.razor did not contain @code marker.")
    text = text.replace(marker, "<OpsAdditionalPanels />\n\n" + marker, 1)

ops.write_text(text, encoding="utf-8")

# Keep Admin and Development, but remove standalone routes for pages that should no longer be in the app surface.
pages_to_deroute = [
    "Operations.razor",
    "Home.razor",
    "HighValueFindings.razor",
    "Status.razor",
    "Targets.razor",
    "AssetGraph.razor",
    "Configuration.razor",
    "TechnologyIdentification.razor",
    "Technologies.razor",
    "WorkerManagement.razor",
]

pages_dir = root / "src/ArgusEngine.CommandCenter.Web/Components/Pages"
for name in pages_to_deroute:
    path = pages_dir / name
    if not path.exists():
        continue
    source = path.read_text(encoding="utf-8")
    source = re.sub(r'(?m)^\s*@page\s+"[^"]+"\s*', '', source)
    path.write_text(source, encoding="utf-8")

# Ensure the realtime client and API clients required by Ops/Admin/Development are registered.
program = root / "src/ArgusEngine.CommandCenter.Web/Program.cs"
if program.exists():
    source = program.read_text(encoding="utf-8")
    if "ArgusEngine.CommandCenter.Realtime" not in source:
        source = "using ArgusEngine.CommandCenter.Realtime;\n" + source
    if "AddScoped<DiscoveryRealtimeClient>" not in source:
        anchor = "builder.Services.AddRazorComponents()"
        registration = "builder.Services.AddScoped<DiscoveryRealtimeClient>();\n"
        if anchor in source:
            source = source.replace(anchor, registration + anchor, 1)
        else:
            source = registration + source
    if "AddHttpClient<WorkerControlApiClient>" not in source and "AddScoped<WorkerControlApiClient>" not in source:
        # Latest Program.cs normally has typed clients; this is a fallback for broken overlay states.
        if "ArgusEngine.CommandCenter.Web.Clients" not in source:
            source = "using ArgusEngine.CommandCenter.Web.Clients;\n" + source
        insertion = 'builder.Services.AddHttpClient<WorkerControlApiClient>(client => client.BaseAddress = new Uri("http://command-center-gateway:8080"));\n'
        anchor = "builder.Services.AddScoped<DiscoveryRealtimeClient>();"
        if anchor in source:
            source = source.replace(anchor, anchor + "\n" + insertion, 1)
        else:
            source = insertion + source
    program.write_text(source, encoding="utf-8")
PY

echo "[argus overlay] Web app page surface restored:"
echo "  - Ops remains at / and /ops using the disk-storage-card OpsRadzen version."
echo "  - High Value and Technology panels are appended at the bottom of Ops."
echo "  - Admin restored at /admin."
echo "  - Development remains at /development."
echo "  - Operations Console and removed standalone pages have no routes."
