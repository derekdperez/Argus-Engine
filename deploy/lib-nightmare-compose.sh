#!/usr/bin/env bash
# Shared helpers for deploy.sh and run-local.sh (source after cd to DotNetSolution root and setting ROOT).
#
# Fast incremental deploy:
#   - Builds only services whose source/deploy fingerprint changed.
#   - Does not pull base images on every deploy by default.
#   - Does not force-recreate unchanged containers by default.
#
# Optional:
#   NIGHTMARE_GIT_PULL=1       Run git pull --ff-only in ROOT before build.
#   NIGHTMARE_NO_CACHE=1       Add docker compose build --no-cache (slowest, strongest cache bust).
#   NIGHTMARE_PULL_IMAGES=1    Add docker compose build --pull. Defaults to 0 for fast deploys.
#   NIGHTMARE_DOCKER_USE_SUDO=1 Prefix docker with sudo (set by lib-install-deps.sh when the daemon socket is not user-accessible).
#   NIGHTMARE_DEPLOY_SKIP_BUILD=1 Set by deploy.sh when all service fingerprints match the last successful deploy.
#   NIGHTMARE_DEPLOY_FRESH=1   Force full rebuild (--no-cache); set by ./deploy.sh -fresh.
#   NIGHTMARE_FORCE_RECREATE=1 Use compose up --force-recreate. Defaults to 0.

nightmare_docker() {
  if [[ "${NIGHTMARE_DOCKER_USE_SUDO:-}" == "1" ]]; then
    sudo docker "$@"
  else
    docker "$@"
  fi
}

nightmare_sha256_file_list() {
  local root="$1"
  shift
  (
    cd "$root"
    for path in "$@"; do
      [[ -e "$path" ]] || continue
      if [[ -d "$path" ]]; then
        find "$path" -type f \
          ! -path '*/bin/*' \
          ! -path '*/obj/*' \
          ! -path '*/out/*' \
          ! -path '*/publish/*' \
          ! -path '*/TestResults/*' \
          -print
      else
        printf '%s\n' "$path"
      fi
    done | LC_ALL=C sort | while IFS= read -r file; do
      if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file"
      else
        shasum -a 256 "$file"
      fi
    done
  ) | {
    if command -v sha256sum >/dev/null 2>&1; then
      sha256sum | awk '{print $1}'
    else
      shasum -a 256 | awk '{print $1}'
    fi
  }
}

nightmare_service_project_path() {
  case "$1" in
    command-center) echo "src/NightmareV2.CommandCenter" ;;
    gatekeeper) echo "src/NightmareV2.Gatekeeper" ;;
    worker-spider) echo "src/NightmareV2.Workers.Spider" ;;
    worker-enum) echo "src/NightmareV2.Workers.Enum" ;;
    worker-portscan) echo "src/NightmareV2.Workers.PortScan" ;;
    worker-highvalue) echo "src/NightmareV2.Workers.HighValue" ;;
    *) return 1 ;;
  esac
}

nightmare_all_dotnet_services() {
  printf '%s\n' \
    command-center \
    gatekeeper \
    worker-spider \
    worker-enum \
    worker-portscan \
    worker-highvalue
}

nightmare_service_fingerprint() {
  local root="$1"
  local service="$2"
  local project_path
  project_path="$(nightmare_service_project_path "$service")"

  local dockerfile="deploy/Dockerfile.worker"
  [[ "$service" == "command-center" ]] && dockerfile="deploy/Dockerfile.web"

  local inputs=(
    "Directory.Build.props"
    "NightmareV2.slnx"
    "deploy/docker-compose.yml"
    "$dockerfile"
    "src/NightmareV2.Application"
    "src/NightmareV2.Contracts"
    "src/NightmareV2.Domain"
    "src/NightmareV2.Infrastructure"
    "$project_path"
  )

  if [[ "$service" != "command-center" ]]; then
    inputs+=("deploy/wordlists")
  fi

  nightmare_sha256_file_list "$root" "${inputs[@]}"
}

nightmare_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-deploy-fingerprints"
}

nightmare_current_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-deploy-fingerprints"
}

nightmare_compute_current_fingerprints() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1
  local out
  out="$(nightmare_current_fingerprint_path)"
  : >"$out"
  local service
  while IFS= read -r service; do
    printf '%s %s\n' "$service" "$(nightmare_service_fingerprint "$root" "$service")" >>"$out"
  done < <(nightmare_all_dotnet_services)
}

nightmare_read_fingerprint() {
  local service="$1"
  local file="$2"
  [[ -f "$file" ]] || return 0
  awk -v svc="$service" '$1 == svc { print $2; exit }' "$file"
}

nightmare_detect_changed_services() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1

  nightmare_compute_current_fingerprints "$root"

  local last_file current_file
  last_file="$(nightmare_fingerprint_path)"
  current_file="$(nightmare_current_fingerprint_path)"

  if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" || ! -f "$last_file" ]]; then
    NIGHTMARE_CHANGED_SERVICES="$(nightmare_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
    export NIGHTMARE_CHANGED_SERVICES
    return 0
  fi

  local changed=()
  local service current last
  while read -r service current; do
    last="$(nightmare_read_fingerprint "$service" "$last_file")"
    if [[ -z "$last" || "$current" != "$last" ]]; then
      changed+=("$service")
    fi
  done <"$current_file"

  NIGHTMARE_CHANGED_SERVICES="${changed[*]:-}"
  export NIGHTMARE_CHANGED_SERVICES
}

nightmare_commit_current_fingerprints() {
  local current_file last_file
  current_file="$(nightmare_current_fingerprint_path)"
  last_file="$(nightmare_fingerprint_path)"
  [[ -f "$current_file" ]] || return 0
  mv -f "$current_file" "$last_file"
}

# Hash of deploy recipes retained for image labels only. It no longer controls whether every service rebuilds.
nightmare_recipe_bundle_hash() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1
  nightmare_sha256_file_list "$root" deploy/docker-compose.yml deploy/Dockerfile.web deploy/Dockerfile.worker deploy/wordlists
}

nightmare_export_build_stamp() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1
  if [[ -d "$root/.git" ]]; then
    local head
    head="$(git -C "$root" rev-parse HEAD 2>/dev/null || echo unknown)"
    if git -C "$root" diff --quiet 2>/dev/null && git -C "$root" diff --cached --quiet 2>/dev/null; then
      export BUILD_SOURCE_STAMP="$head"
    else
      export BUILD_SOURCE_STAMP="${head}-dirty"
    fi
  else
    export BUILD_SOURCE_STAMP="nogit"
  fi
  local recipe
  recipe="$(nightmare_recipe_bundle_hash "$root")"
  export BUILD_SOURCE_STAMP="${BUILD_SOURCE_STAMP}+${recipe:0:16}"
  echo "BUILD_SOURCE_STAMP=${BUILD_SOURCE_STAMP}"
}

nightmare_last_deploy_stamp_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-deploy-stamp"
}

nightmare_write_last_deploy_stamp() {
  local p
  p="$(nightmare_last_deploy_stamp_path)"
  printf '%s\n' "${BUILD_SOURCE_STAMP}" >"$p.tmp"
  mv -f "$p.tmp" "$p"
}

nightmare_decide_incremental_deploy() {
  unset NIGHTMARE_DEPLOY_SKIP_BUILD

  if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]]; then
    export NIGHTMARE_NO_CACHE=1
    nightmare_detect_changed_services "$ROOT"
    echo "Fresh deploy: rebuilding all service images with --no-cache."
    return 0
  fi

  nightmare_detect_changed_services "$ROOT"
  if [[ -z "${NIGHTMARE_CHANGED_SERVICES:-}" ]]; then
    export NIGHTMARE_DEPLOY_SKIP_BUILD=1
    echo "Fast deploy: no service source fingerprints changed; skipping docker compose build."
    echo "  (Use ./deploy/deploy.sh -fresh to force a full rebuild.)"
  else
    echo "Fast deploy: rebuilding changed service image(s): ${NIGHTMARE_CHANGED_SERVICES}"
  fi
}

nightmare_maybe_git_pull() {
  local root="${1:-}"
  [[ "${NIGHTMARE_GIT_PULL:-}" == "1" ]] || return 0
  [[ -d "$root/.git" ]] || { echo "NIGHTMARE_GIT_PULL=1 but $root has no .git; skipping pull." >&2; return 0; }
  echo "NIGHTMARE_GIT_PULL=1: git pull --ff-only in $root"
  git -C "$root" pull --ff-only
}

compose() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  # Compose v2 can delegate multi-service builds to "bake", which has had stability issues on some
  # Linux installs (opaque "failed to execute bake: exit status 1"). Default off; set COMPOSE_BAKE=true to opt in.
  export COMPOSE_BAKE="${COMPOSE_BAKE:-false}"
  local cf="$ROOT/deploy/docker-compose.yml"
  if nightmare_docker compose version >/dev/null 2>&1; then
    nightmare_docker compose -f "$cf" "$@"
  elif command -v docker-compose >/dev/null 2>&1; then
    if [[ "${NIGHTMARE_DOCKER_USE_SUDO:-}" == "1" ]]; then
      sudo docker-compose -f "$cf" "$@"
    else
      docker-compose -f "$cf" "$@"
    fi
  else
    echo "Docker Compose is not available (need 'docker compose' or docker-compose)." >&2
    exit 1
  fi
}

nightmare_compose_build() {
  local args=(build)
  [[ "${NIGHTMARE_PULL_IMAGES:-0}" == "1" || "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--pull)
  [[ "${NIGHTMARE_NO_CACHE:-}" == "1" ]] && args+=(--no-cache)

  if [[ -n "${NIGHTMARE_CHANGED_SERVICES:-}" ]]; then
    # shellcheck disable=SC2206
    local services=( ${NIGHTMARE_CHANGED_SERVICES} )
    args+=("${services[@]}")
  fi

  compose "${args[@]}"
}

nightmare_compose_up_redeploy() {
  local args=(up -d --remove-orphans)
  [[ "${NIGHTMARE_FORCE_RECREATE:-0}" == "1" || "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--force-recreate)

  if [[ -n "${NIGHTMARE_CHANGED_SERVICES:-}" && "${NIGHTMARE_DEPLOY_FRESH:-0}" != "1" ]]; then
    # Bring the full stack up, but only force/recreate changed services when their image changed.
    # Compose will leave unchanged services alone unless their config/image digest changed.
    compose "${args[@]}"
  else
    compose "${args[@]}"
  fi
}

nightmare_compose_deploy_all() {
  if [[ "${NIGHTMARE_DEPLOY_SKIP_BUILD:-}" == "1" ]]; then
    nightmare_compose_up_redeploy
  else
    nightmare_compose_build
    nightmare_compose_up_redeploy
  fi
  nightmare_commit_current_fingerprints
  nightmare_write_last_deploy_stamp
}

nightmare_compose_full_redeploy() {
  nightmare_compose_deploy_all
}
