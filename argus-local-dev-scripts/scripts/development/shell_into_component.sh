#!/usr/bin/env bash
# Open a shell inside a local Argus Compose service container.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/development/common.sh
. "$SCRIPT_DIR/common.sh"

SERVICE="${1:-}"
SHELL_CMD="${2:-sh}"

if [[ -z "$SERVICE" || "$SERVICE" == "-h" || "$SERVICE" == "--help" ]]; then
  cat <<'EOF'
Usage:
  ./scripts/development/shell_into_component.sh SERVICE [shell]

Examples:
  ./scripts/development/shell_into_component.sh command-center sh
  ./scripts/development/shell_into_component.sh postgres sh
  ./scripts/development/shell_into_component.sh worker-enum sh
EOF
  exit 0
fi

cd "$ARGUS_DEV_ROOT"
argus_dev_compose exec "$SERVICE" "$SHELL_CMD"
