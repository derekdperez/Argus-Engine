#!/usr/bin/env bash
# Shared helpers for deploy.sh and run-local.sh (source after cd to DotNetSolution root and setting ROOT).
#
# Fast feedback loop features:
#   - Fingerprints source inputs and image/deploy recipe inputs separately.
#   - Builds only service images whose source/shared dependencies or image recipe changed.
#   - Supports explicit hot-swap mode for running containers: publish the changed .NET service with a
#     cached NuGet folder, copy the publish output into the running container, and restart that service.
#   - Does not pull base images or force-recreate unchanged containers unless requested.
#
# Optional:
#   NIGHTMARE_DEPLOY_MODE=image|hot  Default image. hot updates running containers when only .NET source changed.
#   NIGHTMARE_GIT_PULL=1             Run git pull --ff-only in ROOT before build.
#   NIGHTMARE_NO_CACHE=1             Add docker compose build --no-cache (slowest, strongest cache bust).
#   NIGHTMARE_PULL_IMAGES=1          Add docker compose build --pull. Defaults to 0 for fast deploys.
#   NIGHTMARE_DOCKER_USE_SUDO=1      Prefix docker with sudo (set by lib-install-deps.sh when daemon socket is not user-accessible).
#   NIGHTMARE_DEPLOY_SKIP_BUILD=1    Set when all service fingerprints match the last successful deploy.
#   NIGHTMARE_DEPLOY_FRESH=1         Force full rebuild (--no-cache); set by ./deploy.sh -fresh.
#   NIGHTMARE_FORCE_RECREATE=1       Use compose up --force-recreate. Defaults to 0.

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
          ! -path '*/.nuget/*' \
          ! -path '*/.hot-publish/*' \
          -print
      else
        printf '%s\n' "$path"
      fi
    done | LC_ALL=C sort | {
      if command -v sha256sum >/dev/null 2>&1; then
        if xargs --help 2>/dev/null | grep -q -- '-d'; then
          xargs -r -d '\n' sha256sum
        else
          while IFS= read -r file; do sha256sum "$file"; done
        fi
      else
        while IFS= read -r file; do shasum -a 256 "$file"; done
      fi
    }
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

nightmare_service_app_dll() {
  case "$1" in
    command-center) echo "NightmareV2.CommandCenter.dll" ;;
    gatekeeper) echo "NightmareV2.Gatekeeper.dll" ;;
    worker-spider) echo "NightmareV2.Workers.Spider.dll" ;;
    worker-enum) echo "NightmareV2.Workers.Enum.dll" ;;
    worker-portscan) echo "NightmareV2.Workers.PortScan.dll" ;;
    worker-highvalue) echo "NightmareV2.Workers.HighValue.dll" ;;
    *) return 1 ;;
  esac
}

nightmare_service_csproj() {
  local project_path
  project_path="$(nightmare_service_project_path "$1")"
  printf '%s/%s.csproj\n' "$project_path" "${project_path##*/}"
}

nightmare_service_dockerfile() {
  case "$1" in
    command-center) echo "deploy/Dockerfile.web" ;;
    worker-enum) echo "deploy/Dockerfile.worker-enum" ;;
    *) echo "deploy/Dockerfile.worker" ;;
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

nightmare_service_source_inputs() {
  local service="$1"
  local project_path
  project_path="$(nightmare_service_project_path "$service")"

  local inputs=(
    "Directory.Build.props"
    "NightmareV2.slnx"
    "src/NightmareV2.Application"
    "src/NightmareV2.Contracts"
    "src/NightmareV2.Domain"
    "src/NightmareV2.Infrastructure"
    "$project_path"
  )

  case "$service" in
    command-center)
      inputs+=("src/Resources/Wordlists/high_value")
      ;;
    worker-highvalue)
      inputs+=("src/Resources/RegexPatterns" "src/Resources/Wordlists/high_value")
      ;;
  esac

  printf '%s\n' "${inputs[@]}"
}

nightmare_service_image_inputs() {
  local service="$1"
  local dockerfile
  dockerfile="$(nightmare_service_dockerfile "$service")"

  local inputs=(
    "deploy/docker-compose.yml"
    "$dockerfile"
  )

  if [[ "$service" == "worker-enum" ]]; then
    inputs+=("deploy/wordlists")
  fi

  printf '%s\n' "${inputs[@]}"
}

nightmare_service_source_fingerprint() {
  local root="$1"
  local service="$2"
  mapfile -t inputs < <(nightmare_service_source_inputs "$service")
  nightmare_sha256_file_list "$root" "${inputs[@]}"
}

nightmare_service_image_fingerprint() {
  local root="$1"
  local service="$2"
  mapfile -t inputs < <(nightmare_service_image_inputs "$service")
  nightmare_sha256_file_list "$root" "${inputs[@]}"
}

nightmare_service_fingerprint() {
  local root="$1"
  local service="$2"
  mapfile -t source_inputs < <(nightmare_service_source_inputs "$service")
  mapfile -t image_inputs < <(nightmare_service_image_inputs "$service")
  nightmare_sha256_file_list "$root" "${source_inputs[@]}" "${image_inputs[@]}"
}

nightmare_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-deploy-fingerprints"
}

nightmare_current_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-deploy-fingerprints"
}

nightmare_source_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-source-fingerprints"
}

nightmare_current_source_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-source-fingerprints"
}

nightmare_image_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-image-fingerprints"
}

nightmare_current_image_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-image-fingerprints"
}

nightmare_compute_current_fingerprints() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1

  local full_out source_out image_out
  full_out="$(nightmare_current_fingerprint_path)"
  source_out="$(nightmare_current_source_fingerprint_path)"
  image_out="$(nightmare_current_image_fingerprint_path)"
  : >"$full_out"
  : >"$source_out"
  : >"$image_out"

  local service
  while IFS= read -r service; do
    printf '%s %s\n' "$service" "$(nightmare_service_fingerprint "$root" "$service")" >>"$full_out"
    printf '%s %s\n' "$service" "$(nightmare_service_source_fingerprint "$root" "$service")" >>"$source_out"
    printf '%s %s\n' "$service" "$(nightmare_service_image_fingerprint "$root" "$service")" >>"$image_out"
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

nightmare_detect_hot_swap_plan() {
  local source_last source_current image_last image_current
  source_last="$(nightmare_source_fingerprint_path)"
  source_current="$(nightmare_current_source_fingerprint_path)"
  image_last="$(nightmare_image_fingerprint_path)"
  image_current="$(nightmare_current_image_fingerprint_path)"

  local hot=()
  local image=()
  local service source_now source_then image_now image_then

  # Without split fingerprints from a previous deploy, be conservative and do a normal image build once.
  if [[ ! -f "$source_last" || ! -f "$image_last" ]]; then
    # shellcheck disable=SC2206
    local services=( ${NIGHTMARE_CHANGED_SERVICES:-} )
    NIGHTMARE_HOT_SWAP_SERVICES=""
    NIGHTMARE_IMAGE_REBUILD_SERVICES="${services[*]:-}"
    export NIGHTMARE_HOT_SWAP_SERVICES NIGHTMARE_IMAGE_REBUILD_SERVICES
    return 0
  fi

  # shellcheck disable=SC2206
  local changed_services=( ${NIGHTMARE_CHANGED_SERVICES:-} )
  for service in "${changed_services[@]}"; do
    source_now="$(nightmare_read_fingerprint "$service" "$source_current")"
    source_then="$(nightmare_read_fingerprint "$service" "$source_last")"
    image_now="$(nightmare_read_fingerprint "$service" "$image_current")"
    image_then="$(nightmare_read_fingerprint "$service" "$image_last")"

    if [[ -z "$image_then" || "$image_now" != "$image_then" ]]; then
      image+=("$service")
    elif [[ -z "$source_then" || "$source_now" != "$source_then" ]]; then
      hot+=("$service")
    else
      image+=("$service")
    fi
  done

  NIGHTMARE_HOT_SWAP_SERVICES="${hot[*]:-}"
  NIGHTMARE_IMAGE_REBUILD_SERVICES="${image[*]:-}"
  export NIGHTMARE_HOT_SWAP_SERVICES NIGHTMARE_IMAGE_REBUILD_SERVICES
}

nightmare_commit_current_fingerprints() {
  local current_file last_file current_source last_source current_image last_image
  current_file="$(nightmare_current_fingerprint_path)"
  last_file="$(nightmare_fingerprint_path)"
  current_source="$(nightmare_current_source_fingerprint_path)"
  last_source="$(nightmare_source_fingerprint_path)"
  current_image="$(nightmare_current_image_fingerprint_path)"
  last_image="$(nightmare_image_fingerprint_path)"

  [[ -f "$current_file" ]] && mv -f "$current_file" "$last_file"
  [[ -f "$current_source" ]] && mv -f "$current_source" "$last_source"
  [[ -f "$current_image" ]] && mv -f "$current_image" "$last_image"
}

# Hash of deploy recipes retained for image labels only. It no longer controls whether every service rebuilds.
nightmare_recipe_bundle_hash() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1
  nightmare_sha256_file_list "$root" deploy/docker-compose.yml deploy/Dockerfile.web deploy/Dockerfile.worker deploy/Dockerfile.worker-enum deploy/wordlists
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
  NIGHTMARE_DEPLOY_MODE="${NIGHTMARE_DEPLOY_MODE:-image}"

  case "$NIGHTMARE_DEPLOY_MODE" in
    image | hot) ;;
    *)
      echo "Invalid NIGHTMARE_DEPLOY_MODE='$NIGHTMARE_DEPLOY_MODE' (expected image or hot)." >&2
      exit 1
      ;;
  esac

  if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]]; then
    export NIGHTMARE_NO_CACHE=1
    export NIGHTMARE_DEPLOY_MODE=image
    nightmare_detect_changed_services "$ROOT"
    echo "Fresh deploy: rebuilding all service images with --no-cache."
    return 0
  fi

  nightmare_detect_changed_services "$ROOT"
  if [[ -z "${NIGHTMARE_CHANGED_SERVICES:-}" ]]; then
    export NIGHTMARE_DEPLOY_SKIP_BUILD=1
    echo "Fast deploy: no service source or image fingerprints changed; skipping docker compose build."
    echo "  (Use ./deploy/deploy.sh -fresh to force a full rebuild.)"
  elif [[ "$NIGHTMARE_DEPLOY_MODE" == "hot" ]]; then
    nightmare_detect_hot_swap_plan
    echo "Hot deploy plan:"
    if [[ -n "${NIGHTMARE_HOT_SWAP_SERVICES:-}" ]]; then
      echo "  hot-swap service(s): ${NIGHTMARE_HOT_SWAP_SERVICES}"
    fi
    if [[ -n "${NIGHTMARE_IMAGE_REBUILD_SERVICES:-}" ]]; then
      echo "  image rebuild service(s): ${NIGHTMARE_IMAGE_REBUILD_SERVICES}"
    fi
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
  # BuildKit enables Dockerfile cache mounts for NuGet and Go module caches.
  export DOCKER_BUILDKIT="${DOCKER_BUILDKIT:-1}"
  export COMPOSE_DOCKER_CLI_BUILD="${COMPOSE_DOCKER_CLI_BUILD:-1}"
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

nightmare_compose_build_service_list() {
  local args=(build)
  [[ "${NIGHTMARE_PULL_IMAGES:-0}" == "1" || "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--pull)
  [[ "${NIGHTMARE_NO_CACHE:-}" == "1" ]] && args+=(--no-cache)
  if [[ $# -gt 0 ]]; then
    args+=("$@")
  fi
  compose "${args[@]}"
}

nightmare_compose_build() {
  if [[ -n "${NIGHTMARE_CHANGED_SERVICES:-}" ]]; then
    # shellcheck disable=SC2206
    local services=( ${NIGHTMARE_CHANGED_SERVICES} )
    nightmare_compose_build_service_list "${services[@]}"
  else
    nightmare_compose_build_service_list
  fi
}

nightmare_compose_up_redeploy() {
  local args=(up -d --remove-orphans)
  [[ "${NIGHTMARE_FORCE_RECREATE:-0}" == "1" || "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--force-recreate)
  compose "${args[@]}"
}

nightmare_publish_service_for_hot_swap() {
  local service="$1"
  local csproj out_rel out_abs uid gid
  csproj="$(nightmare_service_csproj "$service")"
  out_rel="deploy/.hot-publish/$service"
  out_abs="$ROOT/$out_rel"
  uid="$(id -u)"
  gid="$(id -g)"

  rm -rf "$out_abs"
  mkdir -p "$out_abs" "$ROOT/.nuget/packages"

  echo "Publishing $service with cached NuGet packages..."
  nightmare_docker run --rm \
    --user "$uid:$gid" \
    -v "$ROOT:/workspace" \
    -w /workspace \
    -e DOTNET_CLI_HOME=/tmp/dotnet-cli \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e NUGET_PACKAGES=/workspace/.nuget/packages \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -lc "dotnet restore '$csproj' && dotnet publish '$csproj' -c Release -o '$out_rel' --no-restore /p:UseAppHost=false"
}

nightmare_hot_swap_services() {
  local fallback=()
  local service cid running out_abs

  for service in "$@"; do
    cid="$(compose ps -q "$service" | tail -n 1 || true)"
    running="false"
    if [[ -n "$cid" ]]; then
      running="$(nightmare_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
    fi

    if [[ -z "$cid" || "$running" != "true" ]]; then
      echo "Hot-swap fallback: $service is not running; it will be rebuilt as an image."
      fallback+=("$service")
      continue
    fi

    nightmare_publish_service_for_hot_swap "$service"
    out_abs="$ROOT/deploy/.hot-publish/$service"
    echo "Copying publish output into $service container and restarting only that service..."
    nightmare_docker cp "$out_abs/." "$cid:/app/"
    compose restart "$service"
  done

  NIGHTMARE_HOT_SWAP_FALLBACK_SERVICES="${fallback[*]:-}"
  export NIGHTMARE_HOT_SWAP_FALLBACK_SERVICES
}

nightmare_compose_image_deploy_for_services() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0
  nightmare_compose_build_service_list "${services[@]}"
  nightmare_compose_up_redeploy
}

nightmare_compose_hot_deploy() {
  # shellcheck disable=SC2206
  local image_services=( ${NIGHTMARE_IMAGE_REBUILD_SERVICES:-} )
  # shellcheck disable=SC2206
  local hot_services=( ${NIGHTMARE_HOT_SWAP_SERVICES:-} )

  if [[ ${#image_services[@]} -gt 0 ]]; then
    nightmare_compose_image_deploy_for_services "${image_services[@]}"
  fi

  if [[ ${#hot_services[@]} -gt 0 ]]; then
    nightmare_hot_swap_services "${hot_services[@]}"
    # shellcheck disable=SC2206
    local fallback_services=( ${NIGHTMARE_HOT_SWAP_FALLBACK_SERVICES:-} )
    if [[ ${#fallback_services[@]} -gt 0 ]]; then
      nightmare_compose_image_deploy_for_services "${fallback_services[@]}"
    fi
  fi
}

nightmare_compose_deploy_all() {
  if [[ "${NIGHTMARE_DEPLOY_SKIP_BUILD:-}" == "1" ]]; then
    nightmare_compose_up_redeploy
  elif [[ "${NIGHTMARE_DEPLOY_MODE:-image}" == "hot" ]]; then
    nightmare_compose_hot_deploy
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
