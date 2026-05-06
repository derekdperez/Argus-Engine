#!/usr/bin/env bash
# Deploy the full Argus Engine stack entirely on the local machine.
#
# Intended use:
#   ./deploy-local.sh
#   ./deploy-local.sh --fresh
#   ./deploy-local.sh status
#   ./deploy-local.sh logs --tail 300 worker-spider
#   ./deploy-local.sh down
#
# This script intentionally disables ECS worker deployment. It runs Postgres,
# Redis, RabbitMQ, Command Center, Gatekeeper, and all worker containers through
# deploy/docker-compose.yml on the current development host.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

find_repo_root() {
  local candidate

  # Explicit override for unusual EC2/dev layouts.
  if [[ -n "${ARGUS_LOCAL_REPO_ROOT:-}" ]]; then
    candidate="${ARGUS_LOCAL_REPO_ROOT%/}"
    if [[ -f "$candidate/deploy/docker-compose.yml" ]]; then
      cd "$candidate" && pwd
      return 0
    fi
  fi

  # Prefer the caller's working directory so this still works when the script is
  # launched from an extracted helper folder such as ./argus-local-dev-scripts.
  for candidate in "$PWD" "$SCRIPT_DIR" "$SCRIPT_DIR/.." "$SCRIPT_DIR/../.." "$SCRIPT_DIR/../../.."; do
    if [[ -f "$candidate/deploy/docker-compose.yml" ]]; then
      cd "$candidate" && pwd
      return 0
    fi
  done

  # Git can fail under sudo on EC2 because of safe.directory ownership checks, so
  # use it only as a final best-effort fallback.
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
MODE="image"
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

APP_SERVICES=(
  command-center
  gatekeeper
  worker-spider
  worker-enum
  worker-portscan
  worker-highvalue
  worker-techid
)

ALL_SERVICES=(
  postgres
  filestore-db-init
  redis
  rabbitmq
  "${APP_SERVICES[@]}"
)

usage() {
  cat <<'EOF'
Usage:
  ./deploy-local.sh [options] [up|status|ps|logs|restart|down|clean|smoke]

Commands:
  up                 Build and run the entire local stack. Default.
  status | ps         Show container status and application health.
  logs               Show/follow Docker Compose logs.
  restart            Restart application containers.
  down               Stop containers and remove orphan containers.
  clean              Stop stack and remove local Compose volumes. Requires confirmation.
  smoke              Run local health/API checks only.

Options:
  --fresh            Pull base images, rebuild with --no-cache, and force recreate containers.
  --hot              Use deploy/deploy.sh hot-swap mode for already-running app containers.
  --skip-build       Run compose up without rebuilding images.
  --pull             Pass --pull to docker compose build.
  --no-cache         Pass --no-cache to docker compose build.
  --force-recreate   Force container recreation during up.
  --no-smoke         Skip post-deploy health/API checks.
  --with-observability
                     Include deploy/docker-compose.observability.yml when present.
  --follow, -f       Follow logs when using the logs command.
  --tail N           Number of log lines for logs command. Default: 200.
  --scale-spider N   Scale local spider workers. Default: 1.
  --scale-enum N     Scale local enum workers. Default: 1.
  --scale-portscan N Scale local port-scan workers. Default: 1.
  --scale-highvalue N
                     Scale local high-value workers. Default: 1.
  --scale-techid N   Scale local technology-identification workers. Default: 1.
  -h, --help         Show this help.

Environment:
  ARGUS_ENGINE_VERSION                  Docker image/component version. Default: local-<git-sha>.
  ARGUS_DIAGNOSTICS_API_KEY             Diagnostics API key. Default: local-dev-diagnostics-key-change-me.
  ARGUS_LOCAL_BASE_URL                  Local base URL for smoke checks. Default: http://127.0.0.1:8080.
  ARGUS_LOCAL_PUBLIC_HOST               Hostname/IP printed for EC2 browser access.
  ARGUS_LOCAL_SCALE_WORKER_SPIDER       Default spider worker scale.
  ARGUS_LOCAL_SCALE_WORKER_ENUM         Default enum worker scale.
  ARGUS_LOCAL_SCALE_WORKER_PORTSCAN     Default port-scan worker scale.
  ARGUS_LOCAL_SCALE_WORKER_HIGHVALUE    Default high-value worker scale.
  ARGUS_LOCAL_SCALE_WORKER_TECHID       Default tech-id worker scale.
EOF
}

die() {
  echo "ERROR: $*" >&2
  exit 1
}

warn() {
  echo "WARN: $*" >&2
}

info() {
  echo "==> $*"
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
      MODE="image"
      BUILD_IMAGES=1
      NO_CACHE=1
      PULL_IMAGES=1
      FORCE_RECREATE=1
      shift
      ;;
    --hot|-hot)
      MODE="hot"
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
    -h|--help)
      usage
      exit 0
      ;;
    *)
      # Anything after "logs" can be a service name.
      if [[ "$CMD" == "logs" ]]; then
        break
      fi
      die "Unknown argument: $1"
      ;;
  esac
done

LOG_SERVICES=("$@")

[[ -f "$COMPOSE_FILE" ]] || die "Missing $COMPOSE_FILE. Run from the repo root or set ARGUS_LOCAL_REPO_ROOT=/path/to/argus-engine."

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
export ARGUS_COMPONENT_UPDATER_ENABLED="${ARGUS_COMPONENT_UPDATER_ENABLED:-true}"
export ARGUS_COMPONENT_UPDATER_REQUIRE_CLEAN_TREE="${ARGUS_COMPONENT_UPDATER_REQUIRE_CLEAN_TREE:-false}"
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

# Reuse the repo's Docker bootstrap helper when it exists.
if [[ -f "$DEPLOY_DIR/lib-install-deps.sh" ]]; then
  # shellcheck source=deploy/lib-install-deps.sh
  . "$DEPLOY_DIR/lib-install-deps.sh"
  if declare -F argus_ensure_runtime_dependencies >/dev/null 2>&1; then
    argus_ensure_runtime_dependencies
  fi
fi

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
  die "Docker Compose is not available. Install Docker Compose v2 or docker-compose v1."
fi

COMPOSE_ARGS=(-f "$COMPOSE_FILE")
if [[ "$WITH_OBSERVABILITY" == "1" ]]; then
  [[ -f "$OBSERVABILITY_COMPOSE_FILE" ]] || die "Missing $OBSERVABILITY_COMPOSE_FILE"
  COMPOSE_ARGS+=(-f "$OBSERVABILITY_COMPOSE_FILE")
fi

compose() {
  "${COMPOSE[@]}" "${COMPOSE_ARGS[@]}" "$@"
}

detect_public_host() {
  if [[ -n "${ARGUS_LOCAL_PUBLIC_HOST:-}" ]]; then
    printf '%s\n' "$ARGUS_LOCAL_PUBLIC_HOST"
    return
  fi

  # EC2 IMDSv2. This is deliberately best-effort and fast-failing.
  if command -v curl >/dev/null 2>&1; then
    local token public_ipv4
    token="$(curl -fsS --max-time 1 -X PUT \
      -H 'X-aws-ec2-metadata-token-ttl-seconds: 60' \
      http://169.254.169.254/latest/api/token 2>/dev/null || true)"
    if [[ -n "$token" ]]; then
      public_ipv4="$(curl -fsS --max-time 1 \
        -H "X-aws-ec2-metadata-token: $token" \
        http://169.254.169.254/latest/meta-data/public-ipv4 2>/dev/null || true)"
      if [[ -n "$public_ipv4" ]]; then
        printf '%s\n' "$public_ipv4"
        return
      fi
    fi
  fi

  printf 'localhost\n'
}

wait_for_ready() {
  local base_url="${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8080}"
  local attempts="${ARGUS_LOCAL_READY_ATTEMPTS:-90}"

  info "Waiting for Command Center readiness: $base_url/health/ready"
  for _ in $(seq 1 "$attempts"); do
    if curl -fsS --max-time 5 "$base_url/health/ready" >/dev/null 2>&1; then
      info "Command Center is ready."
      return 0
    fi
    sleep 2
  done

  warn "Command Center did not become ready."
  compose ps || true
  compose logs --tail=150 command-center || true
  return 1
}

api_get() {
  local path="$1"
  local base_url="${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8080}"
  if command -v jq >/dev/null 2>&1; then
    curl -fsS --max-time 15 "$base_url$path" | jq .
  else
    curl -fsS --max-time 15 "$base_url$path"
    echo
  fi
}

smoke() {
  local base_url="${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8080}"

  info "Running local smoke checks against $base_url"
  curl -fsS --max-time 15 "$base_url/health/ready" >/dev/null

  for path in \
    /api/status/summary \
    /api/workers/health \
    /api/http-request-queue/metrics
  do
    echo ""
    echo "### GET $path"
    if ! api_get "$path"; then
      warn "Smoke endpoint failed: $path"
    fi
  done
}

print_urls() {
  local host
  host="$(detect_public_host)"
  echo ""
  echo "Argus Engine local development stack is running."
  echo "  Command Center: http://localhost:8080/"
  if [[ "$host" != "localhost" ]]; then
    echo "  EC2/public URL: http://$host:8080/"
  fi
  echo "  RabbitMQ UI:    http://localhost:15672/  user/pass: argus / argus"
  echo "  Postgres:       localhost:5432 db=argus_engine user=argus password=argus"
  echo "  Redis:          localhost:6379"
  echo ""
  echo "Useful commands:"
  echo "  ./scripts/development/show_application_state.sh"
  echo "  ./scripts/development/show_development_machine_logs.sh --errors"
  echo "  ./scripts/development/deploy_updated_components.sh --hot"
  echo "  ./deploy-local.sh logs --follow worker-spider"
}

build_images() {
  [[ "$BUILD_IMAGES" == "1" ]] || {
    info "Skipping image build."
    return 0
  }

  local build_args=(build)
  [[ "$PULL_IMAGES" == "1" ]] && build_args+=(--pull)
  [[ "$NO_CACHE" == "1" ]] && build_args+=(--no-cache)

  info "Building local application images. ARGUS_ENGINE_VERSION=$ARGUS_ENGINE_VERSION"
  compose "${build_args[@]}" "${APP_SERVICES[@]}"
}

up_stack() {
  if [[ "$MODE" == "hot" ]]; then
    [[ -x "$DEPLOY_DIR/deploy.sh" ]] || die "Hot mode requires executable $DEPLOY_DIR/deploy.sh"
    info "Running local hot-swap deploy through deploy/deploy.sh."
    argus_ECS_WORKERS=0 ARGUS_ECS_WORKERS=0 "$DEPLOY_DIR/deploy.sh" --hot
    [[ "$RUN_SMOKE" == "1" ]] && smoke
    print_urls
    return 0
  fi

  build_images

  local up_args=(up -d --remove-orphans)
  [[ "$FORCE_RECREATE" == "1" ]] && up_args+=(--force-recreate)
  [[ "$BUILD_IMAGES" == "0" ]] && up_args+=(--no-build)

  up_args+=(
    --scale "worker-spider=$SCALE_WORKER_SPIDER"
    --scale "worker-enum=$SCALE_WORKER_ENUM"
    --scale "worker-portscan=$SCALE_WORKER_PORTSCAN"
    --scale "worker-highvalue=$SCALE_WORKER_HIGHVALUE"
    --scale "worker-techid=$SCALE_WORKER_TECHID"
  )

  info "Starting full local stack."
  compose "${up_args[@]}" "${ALL_SERVICES[@]}"

  wait_for_ready
  [[ "$RUN_SMOKE" == "1" ]] && smoke
  print_urls
}

show_status() {
  compose ps
  echo ""
  smoke || true
}

show_logs() {
  local args=(logs "--tail=$LOG_TAIL")
  [[ "$FOLLOW_LOGS" == "1" ]] && args+=(-f)
  if [[ ${#LOG_SERVICES[@]} -gt 0 ]]; then
    args+=("${LOG_SERVICES[@]}")
  fi
  compose "${args[@]}"
}

case "$CMD" in
  up)
    up_stack
    ;;
  status|ps)
    show_status
    ;;
  smoke)
    smoke
    ;;
  logs)
    show_logs
    ;;
  restart)
    info "Restarting application containers."
    compose restart "${APP_SERVICES[@]}"
    [[ "$RUN_SMOKE" == "1" ]] && smoke
    ;;
  down)
    info "Stopping local stack."
    compose down --remove-orphans
    ;;
  clean)
    [[ "${CONFIRM_RESET_ARGUS_LOCAL:-}" == "yes" ]] || die "Set CONFIRM_RESET_ARGUS_LOCAL=yes to remove containers and volumes."
    info "Removing local containers and volumes."
    compose down --remove-orphans --volumes
    ;;
  *)
    die "Unknown command: $CMD"
    ;;
esac
