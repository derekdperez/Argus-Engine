#!/usr/bin/env bash

# Split-first local deployment for Argus Engine.
#
# This script intentionally targets the refactored CommandCenter services in
# deploy/docker-compose.yml. It does not start or depend on the legacy
# ArgusEngine.CommandCenter monolith.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

find_repo_root() {
  local candidate

  if [[ -n "${ARGUS_LOCAL_REPO_ROOT:-}" ]]; then
    candidate="${ARGUS_LOCAL_REPO_ROOT%/}"
    if [[ -f "$candidate/deploy/docker-compose.yml" ]]; then
      cd "$candidate" && pwd
      return 0
    fi
  fi

  for candidate in "$PWD" "$SCRIPT_DIR" "$SCRIPT_DIR/.." "$SCRIPT_DIR/../.." "$SCRIPT_DIR/../../.."; do
    if [[ -f "$candidate/deploy/docker-compose.yml" ]]; then
      cd "$candidate" && pwd
      return 0
    fi
  done

  if command -v git >/dev/null 2>&1; then
    candidate="$(git -C "$PWD" rev-parse --show-toplevel 2>/dev/null || true)"
    if [[ -n "$candidate" && -f "$candidate/deploy/docker-compose.yml" ]]; then
      cd "$candidate" && pwd
      return 0
    fi
  fi

  return 1
}

ROOT="$(find_repo_root || true)"
[[ -n "$ROOT" ]] || {
  echo "ERROR: Could not locate Argus-Engine repo root. Run from the repo root or set ARGUS_LOCAL_REPO_ROOT=/path/to/argus-engine." >&2
  exit 1
}

DEPLOY_DIR="$ROOT/deploy"
COMPOSE_FILE="$DEPLOY_DIR/docker-compose.yml"
OBSERVABILITY_COMPOSE_FILE="$DEPLOY_DIR/docker-compose.observability.yml"

CMD="up"
BUILD_IMAGES=1
NO_CACHE=0
PULL_IMAGES=0
FORCE_RECREATE=0
RUN_SMOKE=1
FOLLOW_LOGS=0
WITH_OBSERVABILITY=0
LOG_TAIL="${ARGUS_LOCAL_LOG_TAIL:-200}"

SCALE_WORKER_SPIDER="${ARGUS_LOCAL_SCALE_WORKER_SPIDER:-1}"
SCALE_WORKER_ENUM="${ARGUS_LOCAL_SCALE_WORKER_ENUM:-1}"
SCALE_WORKER_PORTSCAN="${ARGUS_LOCAL_SCALE_WORKER_PORTSCAN:-1}"
SCALE_WORKER_HIGHVALUE="${ARGUS_LOCAL_SCALE_WORKER_HIGHVALUE:-1}"
SCALE_WORKER_TECHID="${ARGUS_LOCAL_SCALE_WORKER_TECHID:-1}"
SCALE_WORKER_HTTP_REQUESTER="${ARGUS_LOCAL_SCALE_WORKER_HTTP_REQUESTER:-1}"

APP_SERVICES=(
  command-center-gateway
  command-center-web
  command-center-discovery-api
  command-center-operations-api
  command-center-worker-control-api
  command-center-maintenance-api
  command-center-updates-api
  command-center-realtime
  command-center-bootstrapper
  command-center-spider-dispatcher
  gatekeeper
  worker-spider
  worker-enum
  worker-portscan
  worker-highvalue
  worker-techid
  worker-http-requester
)

usage() {
  cat <<'EOF'
Usage: ./deploy-local.sh [options] [up|status|ps|logs|restart|down|clean|smoke]

Commands:
  up          Build and run the split-first local stack. Default.
  status|ps   Show container status and run smoke checks.
  logs        Show/follow Docker Compose logs.
  restart     Restart application containers.
  down        Stop containers and remove orphan containers.
  clean       Stop stack and remove local Compose volumes. Requires confirmation.
  smoke       Run local health/API checks only.

Options:
  --fresh                Pull base images, rebuild with --no-cache, and force recreate containers.
  --skip-build           Run compose up without rebuilding images.
  --pull                 Pass --pull to docker compose build.
  --no-cache             Pass --no-cache to docker compose build.
  --force-recreate       Force container recreation during up.
  --no-smoke             Skip post-deploy health/API checks.
  --with-observability   Include deploy/docker-compose.observability.yml when present.
  --follow, -f           Follow logs when using the logs command.
  --tail N               Number of log lines for logs command. Default: 200.
  --scale-spider N
  --scale-enum N
  --scale-portscan N
  --scale-highvalue N
  --scale-techid N
  --scale-http-requester N
  -h, --help             Show this help.

Environment:
  ARGUS_ENGINE_VERSION          Docker image/component version. Default: local-<git-sha>.
  ARGUS_LOCAL_BASE_URL          Gateway base URL for smoke checks. Default: http://127.0.0.1:8081.
  ARGUS_DIAGNOSTICS_API_KEY     Diagnostics API key. Default: local-dev-diagnostics-key-change-me.
EOF
}

die() {
  echo "ERROR: $*" >&2
  exit 1
}

info() {
  echo "==> $*"
}

warn() {
  echo "WARN: $*" >&2
}

load_env_file() {
  local file="$1"
  if [[ -f "$file" ]]; then
    info "Loading environment: $file"
    set -a
    # shellcheck disable=SC1090
    . "$file"
    set +a
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    up|status|ps|logs|restart|down|clean|smoke)
      CMD="$1"
      shift
      ;;
    --fresh|-fresh)
      BUILD_IMAGES=1
      NO_CACHE=1
      PULL_IMAGES=1
      FORCE_RECREATE=1
      shift
      ;;
    --skip-build)
      BUILD_IMAGES=0
      shift
      ;;
    --pull)
      PULL_IMAGES=1
      shift
      ;;
    --no-cache)
      NO_CACHE=1
      shift
      ;;
    --force-recreate)
      FORCE_RECREATE=1
      shift
      ;;
    --no-smoke)
      RUN_SMOKE=0
      shift
      ;;
    --with-observability)
      WITH_OBSERVABILITY=1
      shift
      ;;
    --follow|-f)
      FOLLOW_LOGS=1
      shift
      ;;
    --tail)
      [[ $# -ge 2 ]] || die "--tail requires a value"
      LOG_TAIL="$2"
      shift 2
      ;;
    --scale-spider)
      [[ $# -ge 2 ]] || die "--scale-spider requires a value"
      SCALE_WORKER_SPIDER="$2"
      shift 2
      ;;
    --scale-enum)
      [[ $# -ge 2 ]] || die "--scale-enum requires a value"
      SCALE_WORKER_ENUM="$2"
      shift 2
      ;;
    --scale-portscan)
      [[ $# -ge 2 ]] || die "--scale-portscan requires a value"
      SCALE_WORKER_PORTSCAN="$2"
      shift 2
      ;;
    --scale-highvalue)
      [[ $# -ge 2 ]] || die "--scale-highvalue requires a value"
      SCALE_WORKER_HIGHVALUE="$2"
      shift 2
      ;;
    --scale-techid)
      [[ $# -ge 2 ]] || die "--scale-techid requires a value"
      SCALE_WORKER_TECHID="$2"
      shift 2
      ;;
    --scale-http-requester)
      [[ $# -ge 2 ]] || die "--scale-http-requester requires a value"
      SCALE_WORKER_HTTP_REQUESTER="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      if [[ "$CMD" == "logs" ]]; then
        break
      fi
      die "Unknown argument: $1"
      ;;
  esac
done

LOG_SERVICES=("$@")

[[ -f "$COMPOSE_FILE" ]] || die "Missing $COMPOSE_FILE."

cd "$ROOT"

load_env_file "$ROOT/.env.local"
load_env_file "$DEPLOY_DIR/.env.local"
load_env_file "$ROOT/.env"

export argus_ECS_WORKERS=0
export ARGUS_ECS_WORKERS=0
export COMPOSE_BAKE="${COMPOSE_BAKE:-false}"
export DOCKER_BUILDKIT="${DOCKER_BUILDKIT:-1}"
export ARGUS_DIAGNOSTICS_API_KEY="${ARGUS_DIAGNOSTICS_API_KEY:-local-dev-diagnostics-key-change-me}"
export NIGHTMARE_DIAGNOSTICS_API_KEY="${NIGHTMARE_DIAGNOSTICS_API_KEY:-$ARGUS_DIAGNOSTICS_API_KEY}"
export POSTGRES_MAX_CONNECTIONS="${POSTGRES_MAX_CONNECTIONS:-300}"

if [[ -z "${ARGUS_ENGINE_VERSION:-}" ]]; then
  if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git_sha="$(git rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
    if ! git diff --quiet HEAD -- 2>/dev/null; then
      dirty_hash="$(git diff --binary HEAD -- 2>/dev/null | sha256sum | awk '{print $1}' | cut -c1-8)"
      git_sha="${git_sha}-dirty-${dirty_hash}"
    fi
    export ARGUS_ENGINE_VERSION="local-${git_sha}"
  else
    export ARGUS_ENGINE_VERSION="local-dev"
  fi
fi

export BUILD_SOURCE_STAMP="${BUILD_SOURCE_STAMP:-$ARGUS_ENGINE_VERSION}"

DOCKER=(docker)
if ! "${DOCKER[@]}" info >/dev/null 2>&1; then
  if command -v sudo >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
    DOCKER=(sudo docker)
  else
    die "Docker is not running or the current user cannot access the Docker daemon."
  fi
fi

if "${DOCKER[@]}" compose version >/dev/null 2>&1; then
  COMPOSE=("${DOCKER[@]}" compose)
elif command -v docker-compose >/dev/null 2>&1 && docker-compose version >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  die "Docker Compose is not available."
fi

COMPOSE_ARGS=(-f "$COMPOSE_FILE")
if [[ "$WITH_OBSERVABILITY" == "1" ]]; then
  if [[ -f "$OBSERVABILITY_COMPOSE_FILE" ]]; then
    COMPOSE_ARGS+=(-f "$OBSERVABILITY_COMPOSE_FILE")
  else
    warn "Observability compose file not found: $OBSERVABILITY_COMPOSE_FILE"
  fi
fi

compose() {
  "${COMPOSE[@]}" "${COMPOSE_ARGS[@]}" "$@"
}

scale_args=(
  --scale "worker-spider=$SCALE_WORKER_SPIDER"
  --scale "worker-enum=$SCALE_WORKER_ENUM"
  --scale "worker-portscan=$SCALE_WORKER_PORTSCAN"
  --scale "worker-highvalue=$SCALE_WORKER_HIGHVALUE"
  --scale "worker-techid=$SCALE_WORKER_TECHID"
  --scale "worker-http-requester=$SCALE_WORKER_HTTP_REQUESTER"
)

smoke() {
  local base_url="${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8081}"
  local max_attempts="${ARGUS_LOCAL_SMOKE_ATTEMPTS:-90}"
  local sleep_seconds="${ARGUS_LOCAL_SMOKE_SLEEP_SECONDS:-2}"
  local attempt=1

  if ! command -v curl >/dev/null 2>&1; then
    warn "curl not found; skipping smoke checks."
    return 0
  fi

  info "Waiting for split CommandCenter Gateway readiness at $base_url/health/ready"
  until curl -fsS "$base_url/health/ready" >/dev/null 2>&1; do
    if (( attempt >= max_attempts )); then
      echo "ERROR: Gateway did not become ready after $max_attempts attempts." >&2
      compose ps || true
      compose logs --tail 200 command-center-gateway command-center-web command-center-operations-api || true
      return 1
    fi
    sleep "$sleep_seconds"
    attempt=$((attempt + 1))
  done

  local endpoints=(
    "/health/ready"
    "/api/gateway/routes"
    "/api/status/summary"
    "/api/discovery/routes"
    "/api/workers/control/routes"
    "/api/maintenance/routes"
  )

  local endpoint
  for endpoint in "${endpoints[@]}"; do
    info "Smoke: GET $endpoint"
    curl -fsS "$base_url$endpoint" >/dev/null
  done

  info "Split CommandCenter smoke checks passed."
}

case "$CMD" in
  up)
    if [[ "$BUILD_IMAGES" == "1" ]]; then
      build_args=()
      [[ "$PULL_IMAGES" == "1" ]] && build_args+=(--pull)
      [[ "$NO_CACHE" == "1" ]] && build_args+=(--no-cache)
      info "Building split-first local images..."
      compose build "${build_args[@]}"
    fi

    up_args=(-d --remove-orphans)
    [[ "$FORCE_RECREATE" == "1" ]] && up_args+=(--force-recreate)

    info "Starting split-first local stack..."
    compose up "${up_args[@]}" "${scale_args[@]}"

    if [[ "$RUN_SMOKE" == "1" ]]; then
      smoke
    fi
    ;;

  status|ps)
    compose ps
    smoke || true
    ;;

  logs)
    log_args=(--tail "$LOG_TAIL")
    [[ "$FOLLOW_LOGS" == "1" ]] && log_args+=(-f)
    if [[ ${#LOG_SERVICES[@]} -gt 0 ]]; then
      compose logs "${log_args[@]}" "${LOG_SERVICES[@]}"
    else
      compose logs "${log_args[@]}"
    fi
    ;;

  restart)
    info "Restarting application services..."
    compose restart "${APP_SERVICES[@]}"
    if [[ "$RUN_SMOKE" == "1" ]]; then
      smoke
    fi
    ;;

  down)
    compose down --remove-orphans
    ;;

  clean)
    read -r -p "This will remove local Argus Engine containers and volumes. Continue? [y/N] " answer
    case "$answer" in
      y|Y|yes|YES)
        compose down --remove-orphans -v
        ;;
      *)
        echo "Aborted."
        ;;
    esac
    ;;

  smoke)
    smoke
    ;;

  *)
    die "Unknown command: $CMD"
    ;;
esac
