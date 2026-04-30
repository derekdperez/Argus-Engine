#!/usr/bin/env bash
# Full local development sanity check for Nightmare v2.
#
# This is intentionally sequential:
#   1. validate compose config
#   2. build images
#   3. start infrastructure
#   4. start app services
#   5. run HTTP smoke tests and print focused logs
#
# Usage:
#   ./deploy/dev-check.sh
#   ./deploy/dev-check.sh --no-build
#   ./deploy/dev-check.sh --fresh
#
# Environment:
#   NIGHTMARE_SKIP_INSTALL=1 by default, so this script verifies Docker but does not install it.
#   NIGHTMARE_DIAGNOSTICS_API_KEY=... must match Nightmare__Diagnostics__ApiKey in compose.
#   BASE_URL=http://host:8080 overrides the smoke-test URL.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

BUILD=1
FRESH=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build)
      BUILD=0
      shift
      ;;
    --fresh | -fresh)
      FRESH=1
      shift
      ;;
    -h | --help)
      sed -n '1,40p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

export COMPOSE_BAKE="${COMPOSE_BAKE:-false}"
export NIGHTMARE_SKIP_INSTALL="${NIGHTMARE_SKIP_INSTALL:-1}"

# shellcheck source=deploy/lib-nightmare-compose.sh
source "$DEPLOY_DIR/lib-nightmare-compose.sh"
# shellcheck source=deploy/lib-install-deps.sh
source "$DEPLOY_DIR/lib-install-deps.sh"

nightmare_ensure_runtime_dependencies
nightmare_export_build_stamp "$ROOT"

printf '\n== 1. Validate Docker Compose config ==\n'
compose config >/tmp/nightmare-v2-compose-rendered.yml
printf 'Rendered compose config: /tmp/nightmare-v2-compose-rendered.yml\n'

if [[ "$BUILD" == "1" ]]; then
  printf '\n== 2. Build images ==\n'
  if [[ "$FRESH" == "1" ]]; then
    compose build --pull --no-cache
  else
    compose build
  fi
else
  printf '\n== 2. Build images ==\n'
  printf 'Skipped by --no-build\n'
fi

printf '\n== 3. Start infrastructure ==\n'
compose up -d postgres filestore-db-init redis rabbitmq

printf '\n== 4. Start application services ==\n'
compose up -d command-center gatekeeper worker-enum worker-spider worker-portscan worker-highvalue worker-techid

printf '\n== 5. Container status ==\n'
compose ps

printf '\n== 6. HTTP smoke test ==\n'
"$DEPLOY_DIR/smoke-test.sh"

printf '\n== 7. Recent error-like log lines ==\n'
"$DEPLOY_DIR/logs.sh" --errors || true

printf '\nDev check complete. Useful follow-ups:\n'
printf '  ./deploy/logs.sh --follow command-center worker-spider\n'
printf '  ./deploy/smoke-test.sh\n'
printf '  ./deploy/run-local.sh down\n'
