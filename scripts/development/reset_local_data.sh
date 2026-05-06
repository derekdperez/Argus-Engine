#!/usr/bin/env bash
# Destroy local Argus containers and volumes.
# This deletes local Postgres/RabbitMQ/Redis data.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/development/common.sh
. "$SCRIPT_DIR/common.sh"

if [[ "${CONFIRM_RESET_ARGUS_LOCAL:-}" != "yes" ]]; then
  cat >&2 <<'EOF'
Refusing to remove local Argus data.

Run this only when you want to delete local Postgres/RabbitMQ/Redis volumes:

  CONFIRM_RESET_ARGUS_LOCAL=yes ./scripts/development/reset_local_data.sh
EOF
  exit 1
fi

cd "$ARGUS_DEV_ROOT"
argus_dev_compose down --remove-orphans --volumes
