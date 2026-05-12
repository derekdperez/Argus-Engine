#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

ops_page="src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor"
ops_css="src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor.css"

echo "[argus overlay] Restoring the existing Ops page from this checkout when possible..."

if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  # The first UI overlay accidentally replaced OpsRadzen.razor with an older page.
  # Restore the repository's own version first, then keep the added bottom panels
  # in the layout/component files from this overlay.
  if git show "HEAD:${ops_page}" 2>/dev/null | grep -q "Asset Storage"; then
    git show "HEAD:${ops_page}" > "${ops_page}"
    echo "[argus overlay] Restored ${ops_page} from HEAD with Asset Storage."
  elif git show "origin/main:${ops_page}" 2>/dev/null | grep -q "Asset Storage"; then
    git show "origin/main:${ops_page}" > "${ops_page}"
    echo "[argus overlay] Restored ${ops_page} from origin/main with Asset Storage."
  else
    echo "[argus overlay] WARNING: could not find an OpsRadzen.razor in git containing 'Asset Storage'."
    echo "[argus overlay] Leaving the current file in place."
  fi

  if git show "HEAD:${ops_css}" >/dev/null 2>&1; then
    git show "HEAD:${ops_css}" > "${ops_css}"
    echo "[argus overlay] Restored ${ops_css} from HEAD."
  fi
else
  echo "[argus overlay] WARNING: not inside a git checkout; cannot restore the previous Ops page automatically."
fi

echo "[argus overlay] Done."
