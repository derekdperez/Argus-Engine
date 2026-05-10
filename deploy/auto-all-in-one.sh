#!/usr/bin/env bash
# Automatic all-in-one local deployment for Argus Engine.
#
# This script is intentionally conservative:
# - It runs everything on the current machine with Docker Compose.
# - It starts one replica of each worker type by default.
# - It asks before risky actions such as git pull, full fresh rebuild, or volume reset.
# - It uses plain/sequential build output so failures are visible.
# - It runs the bootstrapper explicitly and verifies health after the stack starts.

set -Eeuo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

AUTO_YES="${ARGUS_AUTO_YES:-0}"
FRESH="${ARGUS_AUTO_FRESH:-0}"
RESET_VOLUMES="${ARGUS_AUTO_RESET_VOLUMES:-0}"
SKIP_GIT_PULL="${ARGUS_AUTO_SKIP_GIT_PULL:-0}"
NO_BUILD="${ARGUS_AUTO_NO_BUILD:-0}"
NO_SMOKE="${ARGUS_AUTO_NO_SMOKE:-0}"
TAIL_LINES="${ARGUS_AUTO_ERROR_TAIL:-800}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    -y|--yes) AUTO_YES=1; shift ;;
    --fresh) FRESH=1; shift ;;
    --reset-volumes|--clean-volumes) RESET_VOLUMES=1; shift ;;
    --skip-git-pull|--no-git-pull) SKIP_GIT_PULL=1; shift ;;
    --skip-build|--no-build) NO_BUILD=1; shift ;;
    --no-smoke) NO_SMOKE=1; shift ;;
    --tail) TAIL_LINES="${2:?--tail requires a number}"; shift 2 ;;
    -h|--help)
      cat <<'EOF'
Usage: deploy/auto-all-in-one.sh [options]

Automatic all-in-one local deployment:
  - Repairs repo permissions when possible.
  - Optionally git-pulls when the checkout is clean.
  - Builds/deploys the full Docker Compose stack sequentially.
  - Starts exactly one of each worker type by default.
  - Runs the bootstrapper explicitly.
  - Restarts app services after bootstrap.
  - Verifies health endpoints and prints contextual errors on failure.

Options:
  -y, --yes              Accept safe defaults.
  --fresh                Full no-cache image rebuild.
  --reset-volumes        Stop stack and remove compose volumes before deploying.
  --skip-git-pull        Do not ask to run git pull --ff-only.
  --skip-build           Apply compose stack without rebuilding images.
  --no-smoke             Skip deploy/smoke-test.sh.
  --tail N               Error log tail lines on failure. Default: 800.

Environment:
  ARGUS_AUTO_YES=1
  ARGUS_AUTO_FRESH=1
  ARGUS_AUTO_RESET_VOLUMES=1
  ARGUS_AUTO_SKIP_GIT_PULL=1
  ARGUS_AUTO_NO_BUILD=1
  ARGUS_AUTO_NO_SMOKE=1
EOF
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
  esac
done

log() { printf '\n\033[1;36m[ARGUS AUTO]\033[0m %s\n' "$*"; }
ok() { printf '\033[1;32m[OK]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[WARN]\033[0m %s\n' "$*" >&2; }
err() { printf '\033[1;31m[ERROR]\033[0m %s\n' "$*" >&2; }

confirm() {
  local prompt="$1"
  local default="${2:-no}"
  if [[ "$AUTO_YES" == "1" ]]; then
    if [[ "$default" == "yes" ]]; then
      ok "$prompt yes"
      return 0
    fi
    warn "$prompt no"
    return 1
  fi

  local suffix="[y/N]"
  [[ "$default" == "yes" ]] && suffix="[Y/n]"
  local answer
  read -r -p "$prompt $suffix " answer || answer=""
  answer="${answer,,}"
  if [[ -z "$answer" ]]; then
    [[ "$default" == "yes" ]]
    return
  fi
  [[ "$answer" == "y" || "$answer" == "yes" ]]
}

run() {
  printf '\033[2m$'
  printf ' %q' "$@"
  printf '\033[0m\n'
  "$@"
}

can_sudo() {
  command -v sudo >/dev/null 2>&1
}

compose_cmd=()
detect_compose() {
  if docker compose version >/dev/null 2>&1; then
    compose_cmd=(docker compose)
  elif command -v docker-compose >/dev/null 2>&1; then
    compose_cmd=(docker-compose)
  elif can_sudo && sudo docker compose version >/dev/null 2>&1; then
    compose_cmd=(sudo docker compose)
  else
    compose_cmd=(docker compose)
  fi
}

compose() {
  "${compose_cmd[@]}" -f "$ROOT/deploy/docker-compose.yml" "$@"
}

is_git_clean() {
  [[ -d "$ROOT/.git" ]] || return 1
  git diff --quiet -- . 2>/dev/null && git diff --cached --quiet -- . 2>/dev/null
}

has_upstream() {
  [[ -d "$ROOT/.git" ]] || return 1
  git rev-parse --abbrev-ref --symbolic-full-name '@{u}' >/dev/null 2>&1
}

repair_permissions() {
  log "Checking repository permissions"
  local user group
  user="$(id -un)"
  group="$(id -gn)"

  local needs_chown=0
  [[ -w "$ROOT" ]] || needs_chown=1
  [[ ! -e "$ROOT/.gitignore" || -w "$ROOT/.gitignore" ]] || needs_chown=1
  [[ -w "$ROOT/deploy" ]] || needs_chown=1

  if [[ "$needs_chown" == "1" ]]; then
    if can_sudo; then
      warn "Some repo files are not writable by $user."
      if confirm "Repair ownership with sudo chown -R $user:$group $ROOT ?" "yes"; then
        run sudo chown -R "$user:$group" "$ROOT"
      else
        warn "Continuing without ownership repair; deployment may fail when scripts write logs/env files."
      fi
    else
      warn "Some repo files are not writable and sudo is not available."
    fi
  fi

  find "$ROOT/deploy" -type f -name '*.sh' -exec chmod u+x {} + 2>/dev/null || true
  [[ -f "$ROOT/deploy/deploy.sh" ]] && chmod u+x "$ROOT/deploy/deploy.sh" 2>/dev/null || true
  ok "Permission check complete"
}

maybe_git_pull() {
  [[ "$SKIP_GIT_PULL" == "1" ]] && return 0
  [[ -d "$ROOT/.git" ]] || return 0
  has_upstream || {
    warn "No upstream branch configured; skipping git pull."
    return 0
  }

  if ! is_git_clean; then
    warn "Working tree has local changes; skipping automatic git pull."
    return 0
  fi

  if confirm "Run git pull --ff-only before deploying?" "yes"; then
    run git pull --ff-only
  fi
}

export_one_worker_defaults() {
  # Compose and helper scripts use these names in different places. Export all of them
  # so the all-in-one path consistently starts one of each worker type.
  export ARGUS_WORKER_SPIDER_REPLICAS="${ARGUS_WORKER_SPIDER_REPLICAS:-1}"
  export ARGUS_WORKER_HTTP_REQUESTER_REPLICAS="${ARGUS_WORKER_HTTP_REQUESTER_REPLICAS:-1}"
  export ARGUS_WORKER_ENUM_REPLICAS="${ARGUS_WORKER_ENUM_REPLICAS:-1}"
  export ARGUS_WORKER_PORTSCAN_REPLICAS="${ARGUS_WORKER_PORTSCAN_REPLICAS:-1}"
  export ARGUS_WORKER_HIGHVALUE_REPLICAS="${ARGUS_WORKER_HIGHVALUE_REPLICAS:-1}"
  export ARGUS_WORKER_TECHID_REPLICAS="${ARGUS_WORKER_TECHID_REPLICAS:-1}"

  export argus_SPIDER_REPLICAS="${argus_SPIDER_REPLICAS:-1}"
  export argus_HTTP_REQUESTER_REPLICAS="${argus_HTTP_REQUESTER_REPLICAS:-1}"
  export argus_ENUM_REPLICAS="${argus_ENUM_REPLICAS:-1}"
  export argus_PORTSCAN_REPLICAS="${argus_PORTSCAN_REPLICAS:-1}"
  export argus_HIGHVALUE_REPLICAS="${argus_HIGHVALUE_REPLICAS:-1}"
  export argus_TECHID_REPLICAS="${argus_TECHID_REPLICAS:-1}"
}

maybe_reset_volumes() {
  if [[ "$RESET_VOLUMES" == "1" ]]; then
    warn "Volume reset requested. This removes local Postgres/RabbitMQ/Redis data."
    if confirm "Continue with compose volume removal?" "no"; then
      run env ARGUS_NO_UI=1 CONFIRM_ARGUS_CLEAN=yes bash "$ROOT/deploy/deploy.sh" clean
    else
      warn "Skipping volume reset."
    fi
  elif [[ "$AUTO_YES" != "1" ]]; then
    if confirm "Reset local compose volumes before deploying? This deletes local app data." "no"; then
      run env ARGUS_NO_UI=1 CONFIRM_ARGUS_CLEAN=yes bash "$ROOT/deploy/deploy.sh" clean
    fi
  fi
}

run_deploy() {
  log "Deploying local all-in-one stack"
  export_one_worker_defaults

  local deploy_args=()
  if [[ "$NO_BUILD" == "1" ]]; then
    deploy_args+=(--skip-build)
  elif [[ "$FRESH" == "1" ]]; then
    deploy_args+=(--fresh)
  else
    deploy_args+=(--image)
  fi

  run env \
    ARGUS_NO_UI=1 \
    argus_DEPLOY_MODE=image \
    argus_BUILD_SEQUENTIAL=1 \
    argus_BUILD_PROGRESS=plain \
    BUILDKIT_PROGRESS=plain \
    COMPOSE_BAKE=false \
    COMPOSE_PARALLEL_LIMIT=2 \
    argus_BUILD_TIMEOUT_MIN=0 \
    ARGUS_WORKER_SPIDER_REPLICAS="$ARGUS_WORKER_SPIDER_REPLICAS" \
    ARGUS_WORKER_HTTP_REQUESTER_REPLICAS="$ARGUS_WORKER_HTTP_REQUESTER_REPLICAS" \
    ARGUS_WORKER_ENUM_REPLICAS="$ARGUS_WORKER_ENUM_REPLICAS" \
    ARGUS_WORKER_PORTSCAN_REPLICAS="$ARGUS_WORKER_PORTSCAN_REPLICAS" \
    ARGUS_WORKER_HIGHVALUE_REPLICAS="$ARGUS_WORKER_HIGHVALUE_REPLICAS" \
    ARGUS_WORKER_TECHID_REPLICAS="$ARGUS_WORKER_TECHID_REPLICAS" \
    bash "$ROOT/deploy/deploy.sh" "${deploy_args[@]}"
}

run_bootstrapper() {
  log "Running database/bootstrap initialization explicitly"
  detect_compose
  compose up -d postgres redis rabbitmq

  # The bootstrapper is safe to run repeatedly and closes the race where APIs/workers
  # start before schema/tables have been created.
  if compose config --services | grep -qx 'command-center-bootstrapper'; then
    if ! compose run --rm command-center-bootstrapper; then
      warn "Bootstrapper failed. Recent bootstrapper logs:"
      compose logs --tail=200 command-center-bootstrapper || true
      return 1
    fi
  else
    warn "command-center-bootstrapper service is not present in compose config."
  fi
}

restart_application_services() {
  log "Restarting application services after bootstrap"
  local services=(
    command-center-gateway
    command-center-operations-api
    command-center-discovery-api
    command-center-worker-control-api
    command-center-maintenance-api
    command-center-updates-api
    command-center-realtime
    command-center-spider-dispatcher
    command-center-web
    gatekeeper
    worker-spider
    worker-http-requester
    worker-enum
    worker-portscan
    worker-highvalue
    worker-techid
  )

  local existing=()
  local configured
  configured="$(compose config --services 2>/dev/null || true)"
  for service in "${services[@]}"; do
    if grep -qx "$service" <<<"$configured"; then
      existing+=("$service")
    fi
  done

  if [[ ${#existing[@]} -gt 0 ]]; then
    compose up -d "${existing[@]}"
  fi
}

health_check_url() {
  local name="$1"
  local url="$2"
  local attempts="${3:-30}"
  local delay="${4:-2}"
  local i status
  for ((i=1; i<=attempts; i++)); do
    status="$(curl -fsS -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    if [[ "$status" =~ ^2 ]]; then
      ok "$name ready: $url"
      return 0
    fi
    sleep "$delay"
  done
  warn "$name did not become ready: $url"
  return 1
}

verify_stack() {
  log "Verifying all-in-one stack"
  detect_compose
  compose ps

  local failures=0
  health_check_url "Gateway" "http://127.0.0.1:8081/health/ready" || failures=$((failures+1))
  health_check_url "Web" "http://127.0.0.1:8082/health/ready" || failures=$((failures+1))
  health_check_url "Operations API" "http://127.0.0.1:8083/health/ready" || failures=$((failures+1))
  health_check_url "Discovery API" "http://127.0.0.1:8084/health/ready" || failures=$((failures+1))
  health_check_url "Worker Control API" "http://127.0.0.1:8085/health/ready" || failures=$((failures+1))
  health_check_url "Maintenance API" "http://127.0.0.1:8086/health/ready" || failures=$((failures+1))
  health_check_url "Updates API" "http://127.0.0.1:8087/health/ready" || failures=$((failures+1))
  health_check_url "Realtime" "http://127.0.0.1:8088/health/ready" || failures=$((failures+1))

  if [[ "$NO_SMOKE" != "1" && -f "$ROOT/deploy/smoke-test.sh" ]]; then
    log "Running smoke test"
    if ! bash "$ROOT/deploy/smoke-test.sh"; then
      failures=$((failures+1))
    fi
  fi

  if [[ "$failures" -ne 0 ]]; then
    warn "$failures verification check(s) failed."
    if [[ -x "$ROOT/deploy/logs.sh" ]]; then
      bash "$ROOT/deploy/logs.sh" --errors --tail "$TAIL_LINES" || true
    else
      compose logs --tail="$TAIL_LINES" || true
    fi
    return 1
  fi

  return 0
}

print_urls() {
  cat <<EOF

Argus Engine all-in-one is running.

  Command Center gateway:  http://localhost:8081/  (use host public IP on EC2)
  Command Center web:      http://localhost:8082/
  RabbitMQ admin:          http://localhost:15672/  (user/pass: argus / argus)
  Postgres:                localhost:5432  db=argus_engine  user=argus
  Redis:                   localhost:6379

Useful commands:
  ./deploy/auto-all-in-one.sh
  ./deploy/deploy.sh logs --tail 200
  ./deploy/logs.sh --errors
  docker compose -f deploy/docker-compose.yml ps
  docker compose -f deploy/docker-compose.yml down

EOF
}

main() {
  log "Automatic all-in-one deployment"
  echo "Repository: $ROOT"

  repair_permissions
  maybe_git_pull

  if [[ "$AUTO_YES" != "1" && "$FRESH" != "1" ]]; then
    if confirm "Use a full fresh rebuild instead of cached incremental image deploy?" "no"; then
      FRESH=1
    fi
  fi

  maybe_reset_volumes

  if [[ "$AUTO_YES" != "1" ]]; then
    echo
    echo "Plan:"
    echo "  - Local Docker Compose all-in-one deployment"
    echo "  - Sequential/plain image build"
    echo "  - One replica of each worker type"
    echo "  - Explicit bootstrapper run"
    echo "  - Health/smoke verification"
    [[ "$FRESH" == "1" ]] && echo "  - Full fresh rebuild"
    [[ "$NO_BUILD" == "1" ]] && echo "  - Skip image build"
    confirm "Proceed?" "yes" || exit 0
  fi

  run_deploy
  run_bootstrapper
  restart_application_services
  verify_stack
  print_urls
}

main "$@"
