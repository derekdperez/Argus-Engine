#!/usr/bin/env bash
# Shared helpers for deploy.sh and run-local.sh (source after cd to DotNetSolution root and setting ROOT).
#
# Fast feedback loop features:
#   - Fingerprints source inputs, image recipe inputs, and runtime compose configuration separately.
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
#   NIGHTMARE_DEPLOY_SKIP_BUILD=1    Set when app images do not need rebuilding for this deploy.
#   NIGHTMARE_DEPLOY_FRESH=1         Force full rebuild (--no-cache); set by ./deploy.sh -fresh.
#   NIGHTMARE_FORCE_RECREATE=1       Use compose up --force-recreate. Defaults to 0.

nightmare_docker() {
  if [[ "${NIGHTMARE_DOCKER_USE_SUDO:-}" == "1" ]]; then
    sudo docker "$@"
  else
    docker "$@"
  fi
}

nightmare_sha256_stdin() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | awk '{print $1}'
  else
    shasum -a 256 | awk '{print $1}'
  fi
}

nightmare_fingerprint_from_lines() {
  printf '%s\n' "$@" | nightmare_sha256_stdin
}

nightmare_unique_services() {
  local seen=" "
  local out=()
  local service
  for service in "$@"; do
    [[ -n "$service" ]] || continue
    if [[ "$seen" != *" $service "* ]]; then
      seen+="$service "
      out+=("$service")
    fi
  done
  echo "${out[*]:-}"
}

nightmare_subtract_services() {
  local remove_list="$1"
  shift
  local out=()
  local service
  for service in "$@"; do
    [[ -n "$service" ]] || continue
    if [[ " $remove_list " != *" $service "* ]]; then
      out+=("$service")
    fi
  done
  echo "${out[*]:-}"
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
  ) | nightmare_sha256_stdin
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

nightmare_common_source_inputs() {
  printf '%s\n' \
    "Directory.Build.props" \
    "Directory.Build.targets" \
    "Directory.Packages.props" \
    "NuGet.config" \
    "global.json" \
    "NightmareV2.slnx" \
    "src/NightmareV2.Application" \
    "src/NightmareV2.Contracts" \
    "src/NightmareV2.Domain" \
    "src/NightmareV2.Infrastructure"
}

nightmare_service_specific_source_inputs() {
  local service="$1"
  local project_path
  project_path="$(nightmare_service_project_path "$service")"

  local inputs=(
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

nightmare_service_source_inputs() {
  local service="$1"
  nightmare_common_source_inputs
  nightmare_service_specific_source_inputs "$service"
}

nightmare_service_image_inputs() {
  local service="$1"
  local dockerfile
  dockerfile="$(nightmare_service_dockerfile "$service")"

  local inputs=(
    ".dockerignore"
    "$dockerfile"
  )

  if [[ "$service" == "worker-enum" ]]; then
    inputs+=("deploy/wordlists")
  fi

  printf '%s\n' "${inputs[@]}"
}

nightmare_runtime_config_inputs() {
  printf '%s\n' \
    "deploy/docker-compose.yml" \
    ".env" \
    "deploy/.env"
}

nightmare_runtime_config_fingerprint() {
  local root="$1"
  mapfile -t inputs < <(nightmare_runtime_config_inputs)
  nightmare_sha256_file_list "$root" "${inputs[@]}"
}

nightmare_service_source_metadata() {
  local service="$1"
  printf '%s\n' \
    "fingerprint-schema=source-v2" \
    "service=$service"
}

nightmare_service_image_metadata() {
  local service="$1"
  local project_path project_dir dockerfile app_dll
  project_path="$(nightmare_service_project_path "$service")"
  project_dir="${project_path#src/}"
  dockerfile="$(nightmare_service_dockerfile "$service")"
  app_dll="$(nightmare_service_app_dll "$service")"

  printf '%s\n' \
    "fingerprint-schema=image-v2" \
    "service=$service" \
    "dockerfile=$dockerfile" \
    "project_dir=$project_dir" \
    "app_dll=$app_dll"

  if [[ "$service" == "worker-enum" ]]; then
    printf '%s\n' \
      "SUBFINDER_PACKAGE=${SUBFINDER_PACKAGE:-github.com/projectdiscovery/subfinder/v2/cmd/subfinder@v2.14.0}" \
      "AMASS_PACKAGE=${AMASS_PACKAGE:-github.com/owasp-amass/amass/v5/cmd/amass@v5.1.1}"
  fi
}

nightmare_service_source_fingerprint_from_hashes() {
  local service="$1"
  local common_file_hash="$2"
  local service_file_hash="$3"
  local metadata_hash
  metadata_hash="$(nightmare_service_source_metadata "$service" | nightmare_sha256_stdin)"
  nightmare_fingerprint_from_lines \
    "fingerprint-schema=service-source-v2" \
    "common-files=$common_file_hash" \
    "service-files=$service_file_hash" \
    "metadata=$metadata_hash"
}

nightmare_service_source_fingerprint() {
  local root="$1"
  local service="$2"
  local common_file_hash service_file_hash
  mapfile -t common_inputs < <(nightmare_common_source_inputs)
  mapfile -t service_inputs < <(nightmare_service_specific_source_inputs "$service")
  common_file_hash="$(nightmare_sha256_file_list "$root" "${common_inputs[@]}")"
  service_file_hash="$(nightmare_sha256_file_list "$root" "${service_inputs[@]}")"
  nightmare_service_source_fingerprint_from_hashes "$service" "$common_file_hash" "$service_file_hash"
}

nightmare_service_image_fingerprint() {
  local root="$1"
  local service="$2"
  local file_hash metadata_hash
  mapfile -t inputs < <(nightmare_service_image_inputs "$service")
  file_hash="$(nightmare_sha256_file_list "$root" "${inputs[@]}")"
  metadata_hash="$(nightmare_service_image_metadata "$service" | nightmare_sha256_stdin)"
  nightmare_fingerprint_from_lines \
    "fingerprint-schema=service-image-v2" \
    "files=$file_hash" \
    "metadata=$metadata_hash"
}

nightmare_service_fingerprint() {
  local root="$1"
  local service="$2"
  local source_hash image_hash
  source_hash="$(nightmare_service_source_fingerprint "$root" "$service")"
  image_hash="$(nightmare_service_image_fingerprint "$root" "$service")"
  nightmare_fingerprint_from_lines \
    "fingerprint-schema=service-full-v2" \
    "service=$service" \
    "source=$source_hash" \
    "image=$image_hash"
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

nightmare_image_build_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-image-build-fingerprints"
}

nightmare_runtime_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-runtime-fingerprint"
}

nightmare_current_runtime_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-runtime-fingerprint"
}

nightmare_compute_current_fingerprints() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1

  local full_out source_out image_out runtime_out
  full_out="$(nightmare_current_fingerprint_path)"
  source_out="$(nightmare_current_source_fingerprint_path)"
  image_out="$(nightmare_current_image_fingerprint_path)"
  runtime_out="$(nightmare_current_runtime_fingerprint_path)"
  : >"$full_out"
  : >"$source_out"
  : >"$image_out"

  local common_source_hash
  mapfile -t common_source_inputs < <(nightmare_common_source_inputs)
  common_source_hash="$(nightmare_sha256_file_list "$root" "${common_source_inputs[@]}")"

  local service source_hash image_hash full_hash service_source_hash
  while IFS= read -r service; do
    mapfile -t service_source_inputs < <(nightmare_service_specific_source_inputs "$service")
    service_source_hash="$(nightmare_sha256_file_list "$root" "${service_source_inputs[@]}")"
    source_hash="$(nightmare_service_source_fingerprint_from_hashes "$service" "$common_source_hash" "$service_source_hash")"
    image_hash="$(nightmare_service_image_fingerprint "$root" "$service")"
    full_hash="$(nightmare_fingerprint_from_lines \
      "fingerprint-schema=service-full-v2" \
      "service=$service" \
      "source=$source_hash" \
      "image=$image_hash")"
    printf '%s %s\n' "$service" "$full_hash" >>"$full_out"
    printf '%s %s\n' "$service" "$source_hash" >>"$source_out"
    printf '%s %s\n' "$service" "$image_hash" >>"$image_out"
  done < <(nightmare_all_dotnet_services)

  printf '%s\n' "$(nightmare_runtime_config_fingerprint "$root")" >"$runtime_out"
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

  local last_file current_file image_build_file runtime_last runtime_current
  last_file="$(nightmare_fingerprint_path)"
  current_file="$(nightmare_current_fingerprint_path)"
  image_build_file="$(nightmare_image_build_fingerprint_path)"
  runtime_last="$(nightmare_runtime_fingerprint_path)"
  runtime_current="$(nightmare_current_runtime_fingerprint_path)"

  if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" || ! -f "$last_file" ]]; then
    NIGHTMARE_CHANGED_SERVICES="$(nightmare_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
  else
    local changed=()
    local service current last
    while read -r service current; do
      last="$(nightmare_read_fingerprint "$service" "$last_file")"
      if [[ -z "$last" || "$current" != "$last" ]]; then
        changed+=("$service")
      fi
    done <"$current_file"
    NIGHTMARE_CHANGED_SERVICES="${changed[*]:-}"
  fi
  export NIGHTMARE_CHANGED_SERVICES

  if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" || ! -f "$image_build_file" ]]; then
    # Conservative first-run behavior for the new state file: materialize every app image once.
    NIGHTMARE_IMAGE_STALE_SERVICES="$(nightmare_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
  else
    local stale=()
    local service current built
    while read -r service current; do
      built="$(nightmare_read_fingerprint "$service" "$image_build_file")"
      if [[ -z "$built" || "$current" != "$built" ]]; then
        stale+=("$service")
      fi
    done <"$current_file"
    NIGHTMARE_IMAGE_STALE_SERVICES="${stale[*]:-}"
  fi
  export NIGHTMARE_IMAGE_STALE_SERVICES

  NIGHTMARE_RUNTIME_CONFIG_CHANGED=0
  if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" || ! -f "$runtime_last" ]]; then
    NIGHTMARE_RUNTIME_CONFIG_CHANGED=1
  elif [[ "$(cat "$runtime_current")" != "$(cat "$runtime_last")" ]]; then
    NIGHTMARE_RUNTIME_CONFIG_CHANGED=1
  fi
  export NIGHTMARE_RUNTIME_CONFIG_CHANGED
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

nightmare_compose_service_running() {
  local service="$1"
  local cid running
  cid="$(compose ps -q "$service" | tail -n 1 || true)"
  [[ -n "$cid" ]] || return 1
  running="$(nightmare_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
  [[ "$running" == "true" ]]
}

nightmare_detect_hot_image_materialization_plan() {
  # Hot-swapped source changes make running containers current, but the named Docker image can
  # remain stale. Rebuild stale images when compose may create/recreate a container from that image.
  # shellcheck disable=SC2206
  local stale_services=( ${NIGHTMARE_IMAGE_STALE_SERVICES:-} )
  # shellcheck disable=SC2206
  local existing_rebuild=( ${NIGHTMARE_IMAGE_REBUILD_SERVICES:-} )
  local materialize=()
  local service

  for service in "${stale_services[@]}"; do
    if [[ "${NIGHTMARE_RUNTIME_CONFIG_CHANGED:-0}" == "1" || "${NIGHTMARE_FORCE_RECREATE:-0}" == "1" ]]; then
      materialize+=("$service")
    elif ! nightmare_compose_service_running "$service"; then
      materialize+=("$service")
    fi
  done

  NIGHTMARE_IMAGE_REBUILD_SERVICES="$(nightmare_unique_services "${existing_rebuild[@]}" "${materialize[@]}")"

  # A service rebuilt as an image must not also be hot-swapped in the same deploy.
  # shellcheck disable=SC2206
  local hot_services=( ${NIGHTMARE_HOT_SWAP_SERVICES:-} )
  NIGHTMARE_HOT_SWAP_SERVICES="$(nightmare_subtract_services "$NIGHTMARE_IMAGE_REBUILD_SERVICES" "${hot_services[@]}")"

  export NIGHTMARE_IMAGE_REBUILD_SERVICES NIGHTMARE_HOT_SWAP_SERVICES
}

nightmare_replace_state_file() {
  local src="$1"
  local dest="$2"
  if [[ -d "$dest" && ! -L "$dest" ]]; then
    rm -rf "$dest"
  fi
  mv -f "$src" "$dest"
}

nightmare_update_image_build_fingerprints() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  local current_file image_build_file tmp_file normalized_file service current
  current_file="$(nightmare_current_fingerprint_path)"
  image_build_file="$(nightmare_image_build_fingerprint_path)"
  tmp_file="${image_build_file}.tmp"
  normalized_file="${image_build_file}.normalized"

  [[ -f "$current_file" ]] || return 0
  if [[ -d "$image_build_file" && ! -L "$image_build_file" ]]; then
    rm -rf "$image_build_file"
  fi
  if [[ -f "$image_build_file" ]]; then
    cp "$image_build_file" "$tmp_file"
  else
    : >"$tmp_file"
  fi

  for service in "${services[@]}"; do
    current="$(nightmare_read_fingerprint "$service" "$current_file")"
    [[ -n "$current" ]] || continue
    awk -v svc="$service" '$1 != svc { print }' "$tmp_file" >"${tmp_file}.next"
    mv -f "${tmp_file}.next" "$tmp_file"
    printf '%s %s\n' "$service" "$current" >>"$tmp_file"
  done

  : >"$normalized_file"
  while IFS= read -r service; do
    current="$(nightmare_read_fingerprint "$service" "$tmp_file")"
    [[ -n "$current" ]] && printf '%s %s\n' "$service" "$current" >>"$normalized_file"
  done < <(nightmare_all_dotnet_services)

  nightmare_replace_state_file "$normalized_file" "$image_build_file"
  rm -f "$tmp_file"
}

nightmare_record_built_service_fingerprints() {
  # shellcheck disable=SC2206
  local services=( ${NIGHTMARE_BUILT_SERVICES:-} )
  [[ ${#services[@]} -gt 0 ]] || return 0
  nightmare_update_image_build_fingerprints "${services[@]}"
}

nightmare_commit_current_fingerprints() {
  local current_file last_file current_source last_source current_image last_image current_runtime last_runtime
  current_file="$(nightmare_current_fingerprint_path)"
  last_file="$(nightmare_fingerprint_path)"
  current_source="$(nightmare_current_source_fingerprint_path)"
  last_source="$(nightmare_source_fingerprint_path)"
  current_image="$(nightmare_current_image_fingerprint_path)"
  last_image="$(nightmare_image_fingerprint_path)"
  current_runtime="$(nightmare_current_runtime_fingerprint_path)"
  last_runtime="$(nightmare_runtime_fingerprint_path)"

  [[ -f "$current_file" ]] && nightmare_replace_state_file "$current_file" "$last_file"
  [[ -f "$current_source" ]] && nightmare_replace_state_file "$current_source" "$last_source"
  [[ -f "$current_image" ]] && nightmare_replace_state_file "$current_image" "$last_image"
  [[ -f "$current_runtime" ]] && nightmare_replace_state_file "$current_runtime" "$last_runtime"
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
  unset NIGHTMARE_HOT_SWAP_SERVICES
  unset NIGHTMARE_IMAGE_REBUILD_SERVICES
  unset NIGHTMARE_BUILT_SERVICES
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
    NIGHTMARE_IMAGE_REBUILD_SERVICES="$(nightmare_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
    export NIGHTMARE_IMAGE_REBUILD_SERVICES
    echo "Fresh deploy: rebuilding all service images with --no-cache."
    return 0
  fi

  nightmare_detect_changed_services "$ROOT"

  if [[ "$NIGHTMARE_DEPLOY_MODE" == "hot" ]]; then
    nightmare_detect_hot_swap_plan
    nightmare_detect_hot_image_materialization_plan

    if [[ -z "${NIGHTMARE_CHANGED_SERVICES:-}" && -z "${NIGHTMARE_IMAGE_REBUILD_SERVICES:-}" ]]; then
      export NIGHTMARE_DEPLOY_SKIP_BUILD=1
      echo "Hot deploy: no unapplied service source or image recipe changes; skipping image build."
      if [[ "${NIGHTMARE_RUNTIME_CONFIG_CHANGED:-0}" == "1" ]]; then
        echo "  Runtime compose configuration changed; docker compose up will apply it."
      elif [[ -n "${NIGHTMARE_IMAGE_STALE_SERVICES:-}" ]]; then
        echo "  Stale images are allowed because their hot-swapped containers are already running."
      fi
      echo "  (Use ./deploy/deploy.sh -fresh to force a full rebuild.)"
      return 0
    fi

    echo "Hot deploy plan:"
    if [[ -n "${NIGHTMARE_HOT_SWAP_SERVICES:-}" ]]; then
      echo "  hot-swap service(s): ${NIGHTMARE_HOT_SWAP_SERVICES}"
    fi
    if [[ -n "${NIGHTMARE_IMAGE_REBUILD_SERVICES:-}" ]]; then
      echo "  image rebuild service(s): ${NIGHTMARE_IMAGE_REBUILD_SERVICES}"
    fi
    if [[ "${NIGHTMARE_RUNTIME_CONFIG_CHANGED:-0}" == "1" ]]; then
      echo "  runtime config changed: compose up will be run after safe image materialization."
    fi
    return 0
  fi

  NIGHTMARE_IMAGE_REBUILD_SERVICES="${NIGHTMARE_IMAGE_STALE_SERVICES:-}"
  export NIGHTMARE_IMAGE_REBUILD_SERVICES

  if [[ -z "${NIGHTMARE_IMAGE_REBUILD_SERVICES:-}" ]]; then
    export NIGHTMARE_DEPLOY_SKIP_BUILD=1
    if [[ -z "${NIGHTMARE_CHANGED_SERVICES:-}" ]]; then
      echo "Fast deploy: no service source or image recipe fingerprints changed; skipping docker compose build."
    else
      echo "Fast deploy: images already match changed service fingerprint(s): ${NIGHTMARE_CHANGED_SERVICES}; skipping docker compose build."
    fi
    if [[ "${NIGHTMARE_RUNTIME_CONFIG_CHANGED:-0}" == "1" ]]; then
      echo "  Runtime compose configuration changed; docker compose up will apply it."
    fi
    echo "  (Use ./deploy/deploy.sh -fresh to force a full rebuild.)"
  else
    if [[ -z "${NIGHTMARE_CHANGED_SERVICES:-}" ]]; then
      echo "Fast deploy: materializing image(s) from previous hot deploy or missing image-build state: ${NIGHTMARE_IMAGE_REBUILD_SERVICES}"
    else
      echo "Fast deploy: rebuilding changed/stale service image(s): ${NIGHTMARE_IMAGE_REBUILD_SERVICES}"
    fi
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

nightmare_note_built_services() {
  local built=("$@")
  # shellcheck disable=SC2206
  local previous=( ${NIGHTMARE_BUILT_SERVICES:-} )
  NIGHTMARE_BUILT_SERVICES="$(nightmare_unique_services "${previous[@]}" "${built[@]}")"
  export NIGHTMARE_BUILT_SERVICES
}

nightmare_compose_build_service_list() {
  local args=(build)
  local built_services=("$@")
  [[ "${NIGHTMARE_PULL_IMAGES:-0}" == "1" || "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--pull)
  [[ "${NIGHTMARE_NO_CACHE:-}" == "1" ]] && args+=(--no-cache)
  if [[ $# -gt 0 ]]; then
    args+=("$@")
  else
    mapfile -t built_services < <(nightmare_all_dotnet_services)
  fi
  compose "${args[@]}"
  nightmare_note_built_services "${built_services[@]}"
}

nightmare_compose_build() {
  local selected_services="${NIGHTMARE_IMAGE_REBUILD_SERVICES:-${NIGHTMARE_CHANGED_SERVICES:-}}"
  if [[ -n "$selected_services" ]]; then
    # shellcheck disable=SC2206
    local services=( ${selected_services} )
    nightmare_compose_build_service_list "${services[@]}"
  else
    nightmare_compose_build_service_list
  fi
}

nightmare_compose_up_redeploy() {
  local args=(up -d --remove-orphans)
  [[ "${NIGHTMARE_FORCE_RECREATE:-0}" == "1" || "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--force-recreate)

  # Plain Docker Compose ignores deploy.replicas unless compatibility mode is used.
  # Keep local/EC2 scaling explicit so deployments start enough consumers by default.
  args+=(
    --scale "worker-enum=${NIGHTMARE_ENUM_REPLICAS:-10}"
    --scale "worker-spider=${NIGHTMARE_SPIDER_REPLICAS:-10}"
  )

  compose "${args[@]}"
}

nightmare_compose_force_recreate_services() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  local args=(up -d --no-deps --force-recreate)
  local service include_enum=0 include_spider=0

  for service in "${services[@]}"; do
    case "$service" in
      worker-enum) include_enum=1 ;;
      worker-spider) include_spider=1 ;;
    esac
  done

  # Keep explicit replica counts when a scaled service is recreated from a rebuilt image.
  [[ "$include_enum" == "1" ]] && args+=(--scale "worker-enum=${NIGHTMARE_ENUM_REPLICAS:-10}")
  [[ "$include_spider" == "1" ]] && args+=(--scale "worker-spider=${NIGHTMARE_SPIDER_REPLICAS:-10}")

  args+=("${services[@]}")
  echo "Forcing recreated container(s) from current rebuilt image(s): ${services[*]}"
  compose "${args[@]}"
}

nightmare_hot_copy_publish_output_to_container() {
  local service="$1"
  local cid="$2"
  local out_abs="$3"
  local temp_dir="/tmp/nightmare-hot-publish-${service}"

  # Copy into a temporary directory first, then replace /app so removed static assets/Razor bundles
  # do not remain from the previous publish output.
  nightmare_docker exec "$cid" sh -lc "rm -rf '$temp_dir' && mkdir -p '$temp_dir'"
  nightmare_docker cp "$out_abs/." "$cid:$temp_dir/"
  nightmare_docker exec "$cid" sh -lc "find /app -mindepth 1 -maxdepth 1 -exec rm -rf {} + && cp -a '$temp_dir'/. /app/ && rm -rf '$temp_dir'"
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
  local cids=()
  local running_cids=()

  for service in "$@"; do
    cids=()
    running_cids=()
    mapfile -t cids < <(compose ps -q "$service" || true)

    for cid in "${cids[@]}"; do
      [[ -n "$cid" ]] || continue
      running="$(nightmare_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
      if [[ "$running" == "true" ]]; then
        running_cids+=("$cid")
      fi
    done

    if [[ ${#running_cids[@]} -eq 0 ]]; then
      echo "Hot-swap fallback: $service has no running container; it will be rebuilt as an image."
      fallback+=("$service")
      continue
    fi

    nightmare_publish_service_for_hot_swap "$service"
    out_abs="$ROOT/deploy/.hot-publish/$service"
    echo "Copying publish output into ${#running_cids[@]} running $service container(s), then restarting that service..."
    for cid in "${running_cids[@]}"; do
      nightmare_hot_copy_publish_output_to_container "$service" "$cid" "$out_abs"
    done
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
  nightmare_compose_force_recreate_services "${services[@]}"
}

nightmare_compose_hot_deploy() {
  # shellcheck disable=SC2206
  local image_services=( ${NIGHTMARE_IMAGE_REBUILD_SERVICES:-} )
  # shellcheck disable=SC2206
  local hot_services=( ${NIGHTMARE_HOT_SWAP_SERVICES:-} )
  local compose_up_ran=0

  if [[ ${#image_services[@]} -gt 0 ]]; then
    nightmare_compose_image_deploy_for_services "${image_services[@]}"
    compose_up_ran=1
  fi

  if [[ ${#hot_services[@]} -gt 0 ]]; then
    nightmare_hot_swap_services "${hot_services[@]}"
    # shellcheck disable=SC2206
    local fallback_services=( ${NIGHTMARE_HOT_SWAP_FALLBACK_SERVICES:-} )
    if [[ ${#fallback_services[@]} -gt 0 ]]; then
      nightmare_compose_image_deploy_for_services "${fallback_services[@]}"
      compose_up_ran=1
    fi
  fi

  if [[ "${NIGHTMARE_RUNTIME_CONFIG_CHANGED:-0}" == "1" && "$compose_up_ran" != "1" ]]; then
    nightmare_compose_up_redeploy
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
    # docker compose up does not always recreate a running container when the image tag is stable
    # (for example nightmare-v2/command-center:local). Force only rebuilt services to use the
    # image that was just produced so website/UI changes are visible after a normal deploy.
    # shellcheck disable=SC2206
    local rebuilt_services=( ${NIGHTMARE_BUILT_SERVICES:-} )
    nightmare_compose_force_recreate_services "${rebuilt_services[@]}"
  fi
  nightmare_record_built_service_fingerprints
  nightmare_commit_current_fingerprints
  nightmare_write_last_deploy_stamp
}

nightmare_compose_full_redeploy() {
  nightmare_compose_deploy_all
}
