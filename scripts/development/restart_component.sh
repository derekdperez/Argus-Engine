#!/usr/bin/env bash
# Restart one or more local Argus Compose services.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/development/common.sh
. "$SCRIPT_DIR/common.sh"

if [[ $# -lt 1 || "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<'EOF'
Usage:
  ./scripts/development/restart_component.sh SERVICE [SERVICE...]

Examples:
  ./scripts/development/restart_component.sh command-center
  ./scripts/development/restart_component.sh worker-spider worker-enum
EOF
  exit 0
fi

cd "$ARGUS_DEV_ROOT"
argus_dev_compose restart "$@"
argus_dev_compose ps "$@"
