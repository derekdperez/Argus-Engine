#!/usr/bin/env bash
# Applies the deploy.sh Quick Deploy menu hook to the current repository.
#
# This is idempotent and intentionally narrow:
#   - adds Q/quick-web dispatch to deploy/deploy.sh
#   - leaves the existing DeployOps Python menu and normal deploy flow intact
#   - does not modify Docker Compose or any application code

set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
DEPLOY_SH="$DEPLOY_DIR/deploy.sh"
QUICK_SH="$DEPLOY_DIR/quick-deploy-web.sh"

if [[ ! -f "$DEPLOY_SH" ]]; then
  echo "ERROR: $DEPLOY_SH not found." >&2
  exit 1
fi

if [[ ! -f "$QUICK_SH" ]]; then
  echo "ERROR: $QUICK_SH not found. Unzip deploy/quick-deploy-web.sh first." >&2
  exit 1
fi

chmod +x "$QUICK_SH"

python3 - "$DEPLOY_SH" <<'PY'
from __future__ import annotations

import re
import sys
from pathlib import Path

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")

marker = "ARGUS_QUICK_DEPLOY_WEB_OPTION_BEGIN"
if marker in text:
    print("deploy.sh already contains the Quick Deploy hook.")
    raise SystemExit(0)

hook = r'''
# ARGUS_QUICK_DEPLOY_WEB_OPTION_BEGIN
argus_quick_deploy_menu() {
  echo ""
  echo "Argus Engine Deploy"
  echo "==================="
  echo "[Q] Quick Deploy"
  echo "[Enter] Full DeployOps menu"
  echo ""
  read -r -p "Choose: " argus_deploy_choice
  case "$argus_deploy_choice" in
    [Qq])
      echo ""
      echo "Quick Deploy"
      echo "============"
      echo "[1] Deploy Web App Only"
      echo "[0] Back/Exit"
      echo ""
      read -r -p "Choose: " argus_quick_choice
      case "$argus_quick_choice" in
        1)
          exec bash "$DEPLOY_DIR/quick-deploy-web.sh"
          ;;
        ""|0)
          exit 0
          ;;
        *)
          echo "Invalid quick deploy choice." >&2
          exit 2
          ;;
      esac
      ;;
    *)
      return 0
      ;;
  esac
}

if [[ $# -gt 0 ]]; then
  case "${1:-}" in
    q|Q|quick|quick-web|quick-deploy|quick-deploy-web)
      shift
      exec bash "$DEPLOY_DIR/quick-deploy-web.sh" "$@"
      ;;
  esac
fi

if [[ "${ARGUS_NO_UI:-0}" != "1" && $# -eq 0 && -t 0 && -t 1 ]]; then
  argus_quick_deploy_menu
fi
# ARGUS_QUICK_DEPLOY_WEB_OPTION_END
'''

pattern = r'(cd "\$ROOT")\s+if \[\[ "\$\{ARGUS_NO_UI:-0\}" != "1" \]\]; then'
replacement = r'\1\n' + hook + r'\nif [[ "${ARGUS_NO_UI:-0}" != "1" ]]; then'
new_text, count = re.subn(pattern, replacement, text, count=1)
if count != 1:
    raise SystemExit("ERROR: Could not find deploy.sh UI dispatch insertion point.")

# Keep help useful for non-interactive users. This is best-effort because the script is
# intentionally tolerant of minified or reformatted deploy.sh variants.
help_pattern = "up (default) Universal incremental deploy."
help_replacement = (
    "up (default) Universal incremental deploy.\\n"
    "quick-web|quick|q Run Quick Deploy > Deploy Web App Only. Publishes, copies, and restarts only command-center-web.\\n"
)
new_text = new_text.replace(help_pattern, help_replacement, 1)

useful_pattern = 'echo " ./deploy/deploy.sh # incremental hot deploy; rebuilds only when needed"'
useful_replacement = (
    'echo " ./deploy/deploy.sh # interactive DeployOps menu with [Q] Quick Deploy" '
    'echo " ./deploy/deploy.sh q # quick deploy: web app only"'
)
new_text = new_text.replace(useful_pattern, useful_replacement, 1)

backup = path.with_suffix(path.suffix + ".quick-deploy-web.bak")
if not backup.exists():
    backup.write_text(text, encoding="utf-8")

path.write_text(new_text, encoding="utf-8")
print("Patched deploy.sh with Quick Deploy menu and CLI dispatch.")
PY

echo ""
echo "Quick Deploy option installed."
echo ""
echo "Usage:"
echo "  ./deploy/deploy.sh"
echo "    then press Q, then 1"
echo ""
echo "  ./deploy/deploy.sh q"
echo "  ./deploy/deploy.sh quick-web"
