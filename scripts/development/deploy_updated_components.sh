#!/usr/bin/env bash
# Fast local/EC2 development deploy for changed Argus components.
#
# Default behavior:
# - If the Command Center container is running, use deploy/deploy.sh --hot.
# - Otherwise, use ./deploy-local.sh up.
# - ECS workers are explicitly disabled.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/development/common.sh
. "$SCRIPT_DIR/common.sh"

MODE="auto"
RUN_STATE=1
PASS_ARGS=()

usage() {
  cat <<'EOF'
Usage:
  ./scripts/development/deploy_updated_components.sh [options]

Options:
  --auto          Hot-swap if stack is already running, otherwise deploy local stack. Default.
  --hot           Force deploy/deploy.sh --hot.
  --image         Force ./deploy-local.sh up.
  --fresh         Force ./deploy-local.sh --fresh up.
  --pull          Pull base images during image/fresh deploy.
  --no-smoke      Skip deploy-local smoke checks.
  --no-state      Do not run show_application_state.sh after deploy.
  -h, --help      Show this help.

Compatibility:
  scripts/development/deploy_updatd_components.sh is also provided as a wrapper
  for the misspelled name.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --auto)
      MODE="auto"
      shift
      ;;
    --hot)
      MODE="hot"
      shift
      ;;
    --image)
      MODE="image"
      shift
      ;;
    --fresh)
      MODE="fresh"
      shift
      ;;
    --pull|--no-smoke)
      PASS_ARGS+=("$1")
      shift
      ;;
    --no-state)
      RUN_STATE=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      argus_dev_die "Unknown argument: $1"
      ;;
  esac
done

cd "$ARGUS_DEV_ROOT"

export argus_ECS_WORKERS=0
export ARGUS_ECS_WORKERS=0
export COMPOSE_BAKE="${COMPOSE_BAKE:-false}"
export ARGUS_COMPONENT_UPDATER_REQUIRE_CLEAN_TREE="${ARGUS_COMPONENT_UPDATER_REQUIRE_CLEAN_TREE:-false}"

command_center_cid="$(argus_dev_container_id command-center || true)"
if [[ "$MODE" == "auto" ]]; then
  if [[ -n "$command_center_cid" ]] && argus_dev_docker inspect -f '{{.State.Running}}' "$command_center_cid" 2>/dev/null | grep -q true; then
    MODE="hot"
  else
    MODE="image"
  fi
fi

case "$MODE" in
  hot)
    [[ -x "$ARGUS_DEV_DEPLOY_DIR/deploy.sh" ]] || argus_dev_die "Missing executable deploy/deploy.sh"
    echo "Running hot-swap deploy against local containers..."
    argus_ECS_WORKERS=0 ARGUS_ECS_WORKERS=0 "$ARGUS_DEV_DEPLOY_DIR/deploy.sh" --hot
    ;;
  image)
    echo "Deploying local stack with image rebuilds..."
    "$ARGUS_DEV_ROOT/deploy-local.sh" up "${PASS_ARGS[@]}"
    ;;
  fresh)
    echo "Deploying local stack with a fresh rebuild..."
    "$ARGUS_DEV_ROOT/deploy-local.sh" --fresh up "${PASS_ARGS[@]}"
    ;;
  *)
    argus_dev_die "Unsupported mode: $MODE"
    ;;
esac

if [[ "$RUN_STATE" == "1" ]]; then
  "$SCRIPT_DIR/show_application_state.sh" --tail 80 || true
fi
