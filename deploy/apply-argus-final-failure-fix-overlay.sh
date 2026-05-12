#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

ops_file="src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor"
ops_css_file="src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor.css"
base_commit="${ARGUS_OPS_BASE_COMMIT:-dd5e740}"

echo "[argus ops fix] Restoring the previous-good Ops page from ${base_commit} and applying append-only panels..."

restore_from_git() {
    local commit="$1"
    local path="$2"
    if git show "${commit}:${path}" > "${path}.tmp"; then
        mv "${path}.tmp" "${path}"
        return 0
    fi

    rm -f "${path}.tmp"
    echo "[argus ops fix] ${commit}:${path} not present locally; fetching recent main history..."
    git fetch --depth=250 origin main >/dev/null 2>&1 || git fetch origin main >/dev/null 2>&1
    git show "${commit}:${path}" > "${path}.tmp"
    mv "${path}.tmp" "${path}"
}

restore_from_git "$base_commit" "$ops_file"
restore_from_git "$base_commit" "$ops_css_file"

python3 - <<'PY'
from pathlib import Path
import re

path = Path("src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor")
text = path.read_text()

# Keep the desired "dash placeholder" summary-card behavior from the previous good Ops page.
text = re.sub(
    r'@if \(_initialLoadStarted && _targetsLoading && TotalTargets == 0 && ConfirmedAssets == 0\) \{\s*Loading\.\.\.\s*—\s*\} else \{',
    '@if (_initialLoadStarted && _targetsLoading && TotalTargets == 0 && ConfirmedAssets == 0) {  Targets — Assets — Subdomains — Unique Technologies — HTTP Queue — Asset Storage — } else {',
    text,
    count=1,
)

# Add the new consolidated grids only at the bottom of the existing Ops screen.
if "<OpsBottomPanels" not in text:
    marker = "@if (_assetDecisionModal is not null) {"
    if marker not in text:
        raise SystemExit("Could not find Asset Admission modal marker in OpsRadzen.razor; refusing to rewrite the Ops page.")
    text = text.replace(marker, '<OpsBottomPanels />\n\n' + marker, 1)

path.write_text(text)
PY

echo "[argus ops fix] Done."
echo "[argus ops fix] Rebuild/redeploy command:"
echo "  ARGUS_NO_UI=1 bash deploy/auto-all-in-one.sh --yes"
