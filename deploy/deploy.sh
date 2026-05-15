#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

if [[ $# -gt 0 ]]; then
  case "${1:-}" in
    q|Q|quick|quick-web|quick-deploy|quick-deploy-web)
      shift
      exec bash "$DEPLOY_DIR/quick-deploy-web.sh" "$@"
      ;;
  esac
fi

if command -v python3 >/dev/null 2>&1; then
  exec python3 "$DEPLOY_DIR/deploy.py" "$@"
fi

echo "ERROR: python3 is required for deploy.sh (standalone deploy.py entrypoint)." >&2
exit 127
