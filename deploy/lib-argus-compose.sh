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
#   argus_DEPLOY_MODE=image|hot  Default image. hot updates running containers when only .NET source changed.
#   argus_GIT_PULL=1             Run git pull --ff-only in ROOT before build.
#   argus_NO_CACHE=1             Add docker compose build --no-cache (slowest, strongest cache bust).
#   argus_PULL_IMAGES=1          Add docker compose build --pull. Defaults to 0 for fast deploys.
#   argus_DOCKER_USE_SUDO=1      Prefix docker with sudo (set by lib-install-deps.sh when daemon socket is not user-accessible).
#   argus_DEPLOY_SKIP_BUILD=1    Set when app images do not need rebuilding for this deploy.
#   argus_DEPLOY_FRESH=1         Force full rebuild (--no-cache); set by ./deploy.sh -fresh.
#   argus_FORCE_RECREATE=1       Use compose up --force-recreate. Defaults to 0.
#   argus_BUILD_TIMEOUT_MIN=0    Max minutes allowed for a compose build invocation (0 disables timeout).
#   argus_BUILD_SEQUENTIAL=0     Build selected services one-by-one instead of one parallel compose build.
#   argus_BUILD_PROGRESS=auto    Build progress style: auto|plain|tty (default auto).

argus_docker() {
  if [[ "${argus_DOCKER_USE_SUDO:-}" == "1" ]]; then
    sudo docker "$@"
  else
    docker "$@"
  fi
}

argus_sha256_stdin() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | awk '{print $1}'
  else
    shasum -a 256 | awk '{print $1}'
  fi
}

argus_fingerprint_from_lines() {
  printf '%s\n' "$@" | argus_sha256_stdin
}

argus_unique_services() {
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

argus_subtract_services() {
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

argus_sha256_file_list() {
  local root="$1"
  shift
  (
    cd "$root"
    local paths=("$@")
    if [[ -d ".git" ]]; then
      # Use git ls-files for directories to respect .gitignore.
      # For individual files, we just list them if they exist (even if ignored).
      for p in "${paths[@]}"; do
        if [[ -d "$p" ]]; then
          git ls-files --cached --others --exclude-standard -- "$p"
        elif [[ -e "$p" ]]; then
          printf '%s\n' "$p"
        fi
      done
    else
      for path in "${paths[@]}"; do
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
      done
    fi | LC_ALL=C sort -u | {
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
  ) | argus_sha256_stdin
}

argus_service_project_path() {
  case "$1" in
    command-center) echo "src/ArgusEngine.CommandCenter" ;;
    gatekeeper) echo "src/ArgusEngine.Gatekeeper" ;;
    worker-spider) echo "src/ArgusEngine.Workers.Spider" ;;
    worker-enum) echo "src/ArgusEngine.Workers.Enumeration" ;;
    worker-portscan) echo "src/ArgusEngine.Workers.PortScan" ;;
    worker-highvalue) echo "src/ArgusEngine.Workers.HighValue" ;;
    worker-techid) echo "src/ArgusEngine.Workers.TechnologyIdentification" ;;
    worker-http-requester) echo "src/ArgusEngine.Workers.HttpRequester" ;;
    *) return 1 ;;
  esac
}

argus_service_app_dll() {
  case "$1" in
    command-center) echo "ArgusEngine.CommandCenter.dll" ;;
    gatekeeper) echo "ArgusEngine.Gatekeeper.dll" ;;
    worker-spider) echo "ArgusEngine.Workers.Spider.dll" ;;
    worker-enum) echo "ArgusEngine.Workers.Enumeration.dll" ;;
    worker-portscan) echo "ArgusEngine.Workers.PortScan.dll" ;;
    worker-highvalue) echo "ArgusEngine.Workers.HighValue.dll" ;;
    worker-techid) echo "ArgusEngine.Workers.TechnologyIdentification.dll" ;;
    worker-http-requester) echo "ArgusEngine.Workers.HttpRequester.dll" ;;
    *) return 1 ;;
  esac
}

argus_service_csproj() {
  local project_path
  project_path="$(argus_service_project_path "$1")"
  printf '%s/%s.csproj\n' "$project_path" "${project_path##*/}"
}

argus_service_dockerfile() {
  case "$1" in
    command-center) echo "deploy/Dockerfile.web" ;;
    worker-enum) echo "deploy/Dockerfile.worker-enum" ;;
    *) echo "deploy/Dockerfile.worker" ;;
  esac
}

argus_all_dotnet_services() {
  printf '%s\n' \
    command-center \
    gatekeeper \
    worker-spider \
    worker-enum \
    worker-portscan \
    worker-highvalue \
    worker-techid \
    worker-http-requester
}

argus_common_source_inputs() {
  printf '%s\n' \
    "Directory.Build.props" \
    "Directory.Build.targets" \
    "Directory.Packages.props" \
    "NuGet.config" \
    "global.json" \
    "ArgusEngine.slnx" \
    "src/ArgusEngine.Application" \
    "src/ArgusEngine.Contracts" \
    "src/ArgusEngine.Domain" \
    "src/ArgusEngine.Infrastructure"
}

argus_service_specific_source_inputs() {
  local service="$1"
  local project_path
  project_path="$(argus_service_project_path "$service")"

  local inputs=(
    "$project_path"
  )

  case "$service" in
    command-center)
      inputs+=(
        "src/ArgusEngine.Harness.Core"
        "src/ArgusEngine.Gatekeeper"
        "src/ArgusEngine.Workers.Enumeration"
        "src/ArgusEngine.Workers.Spider"
        "src/ArgusEngine.Workers.PortScan"
        "src/ArgusEngine.Workers.HighValue"
        "src/ArgusEngine.Workers.TechnologyIdentification"
        "src/ArgusEngine.Workers.HttpRequester"
        "src/Resources/Wordlists/high_value"
      )
      ;;
    worker-highvalue)
      inputs+=("src/Resources/RegexPatterns" "src/Resources/Wordlists/high_value")
      ;;
    worker-techid)
      inputs+=("src/Resources/TechIdentificationData" "src/Resources/TechnologyDetection")
      ;;
  esac

  printf '%s\n' "${inputs[@]}"
}

argus_service_source_inputs() {
  local service="$1"
  argus_common_source_inputs
  argus_service_specific_source_inputs "$service"
}

argus_service_image_inputs() {
  local service="$1"
  local dockerfile
  dockerfile="$(argus_service_dockerfile "$service")"

  local inputs=(
    ".dockerignore"
    "$dockerfile"
  )

  if [[ "$service" == "worker-enum" ]]; then
    inputs+=("deploy/wordlists" "deploy/artifacts/recon-tools")
  fi

  printf '%s\n' "${inputs[@]}"
}

argus_runtime_config_inputs() {
  printf '%s\n' \
    "deploy/docker-compose.yml" \
    ".env" \
    "deploy/.env"
}

argus_runtime_config_fingerprint() {
  local root="$1"
  mapfile -t inputs < <(argus_runtime_config_inputs)
  argus_sha256_file_list "$root" "${inputs[@]}"
}

argus_service_source_metadata() {
  local service="$1"
  printf '%s\n' \
    "fingerprint-schema=source-v2" \
    "service=$service"
}

argus_service_image_metadata() {
  local service="$1"
  local project_path project_dir dockerfile app_dll
  project_path="$(argus_service_project_path "$service")"
  project_dir="${project_path#src/}"
  dockerfile="$(argus_service_dockerfile "$service")"
  app_dll="$(argus_service_app_dll "$service")"

  printf '%s\n' \
    "fingerprint-schema=image-v2" \
    "service=$service" \
    "dockerfile=$dockerfile" \
    "project_dir=$project_dir" \
    "app_dll=$app_dll"

  if [[ "$service" == "worker-enum" ]]; then
    printf '%s\n' \
      "SUBFINDER_VERSION=${SUBFINDER_VERSION:-2.14.0}" \
      "AMASS_VERSION=${AMASS_VERSION:-5.1.1}"
  fi
}

argus_service_source_fingerprint_from_hashes() {
  local service="$1"
  local common_file_hash="$2"
  local service_file_hash="$3"
  local metadata_hash
  metadata_hash="$(argus_service_source_metadata "$service" | argus_sha256_stdin)"
  argus_fingerprint_from_lines \
    "fingerprint-schema=service-source-v2" \
    "common-files=$common_file_hash" \
    "service-files=$service_file_hash" \
    "metadata=$metadata_hash"
}

argus_service_source_fingerprint() {
  local root="$1"
  local service="$2"
  local common_file_hash service_file_hash
  mapfile -t common_inputs < <(argus_common_source_inputs)
  mapfile -t service_inputs < <(argus_service_specific_source_inputs "$service")
  common_file_hash="$(argus_sha256_file_list "$root" "${common_inputs[@]}")"
  service_file_hash="$(argus_sha256_file_list "$root" "${service_inputs[@]}")"
  argus_service_source_fingerprint_from_hashes "$service" "$common_file_hash" "$service_file_hash"
}

argus_service_image_fingerprint() {
  local root="$1"
  local service="$2"
  local file_hash metadata_hash
  mapfile -t inputs < <(argus_service_image_inputs "$service")
  file_hash="$(argus_sha256_file_list "$root" "${inputs[@]}")"
  metadata_hash="$(argus_service_image_metadata "$service" | argus_sha256_stdin)"
  argus_fingerprint_from_lines \
    "fingerprint-schema=service-image-v2" \
    "files=$file_hash" \
    "metadata=$metadata_hash"
}

argus_service_fingerprint() {
  local root="$1"
  local service="$2"
  local source_hash image_hash
  source_hash="$(argus_service_source_fingerprint "$root" "$service")"
  image_hash="$(argus_service_image_fingerprint "$root" "$service")"
  argus_fingerprint_from_lines \
    "fingerprint-schema=service-full-v2" \
    "service=$service" \
    "source=$source_hash" \
    "image=$image_hash"
}

argus_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-deploy-fingerprints"
}

argus_current_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-deploy-fingerprints"
}

argus_source_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-source-fingerprints"
}

argus_current_source_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-source-fingerprints"
}

argus_image_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-image-fingerprints"
}

argus_current_image_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-image-fingerprints"
}

argus_image_build_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-image-build-fingerprints"
}

argus_runtime_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-runtime-fingerprint"
}

argus_current_runtime_fingerprint_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.current-runtime-fingerprint"
}

argus_compute_current_fingerprints() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1

  local full_out source_out image_out runtime_out
  full_out="$(argus_current_fingerprint_path)"
  source_out="$(argus_current_source_fingerprint_path)"
  image_out="$(argus_current_image_fingerprint_path)"
  runtime_out="$(argus_current_runtime_fingerprint_path)"
  : >"$full_out"
  : >"$source_out"
  : >"$image_out"

  local common_source_hash
  mapfile -t common_source_inputs < <(argus_common_source_inputs)
  common_source_hash="$(argus_sha256_file_list "$root" "${common_source_inputs[@]}")"

  local service source_hash image_hash full_hash service_source_hash
  while IFS= read -r service; do
    mapfile -t service_source_inputs < <(argus_service_specific_source_inputs "$service")
    service_source_hash="$(argus_sha256_file_list "$root" "${service_source_inputs[@]}")"
    source_hash="$(argus_service_source_fingerprint_from_hashes "$service" "$common_source_hash" "$service_source_hash")"
    image_hash="$(argus_service_image_fingerprint "$root" "$service")"
    full_hash="$(argus_fingerprint_from_lines \
      "fingerprint-schema=service-full-v2" \
      "service=$service" \
      "source=$source_hash" \
      "image=$image_hash")"
    printf '%s %s\n' "$service" "$full_hash" >>"$full_out"
    printf '%s %s\n' "$service" "$source_hash" >>"$source_out"
    printf '%s %s\n' "$service" "$image_hash" >>"$image_out"
  done < <(argus_all_dotnet_services)

  printf '%s\n' "$(argus_runtime_config_fingerprint "$root")" >"$runtime_out"
}

argus_read_fingerprint() {
  local service="$1"
  local file="$2"
  [[ -f "$file" ]] || return 0
  awk -v svc="$service" '$1 == svc { print $2; exit }' "$file"
}

argus_detect_changed_services() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1

  argus_compute_current_fingerprints "$root"

  local last_file current_file image_build_file runtime_last runtime_current
  last_file="$(argus_fingerprint_path)"
  current_file="$(argus_current_fingerprint_path)"
  image_build_file="$(argus_image_build_fingerprint_path)"
  runtime_last="$(argus_runtime_fingerprint_path)"
  runtime_current="$(argus_current_runtime_fingerprint_path)"

  if [[ "${argus_DEPLOY_FRESH:-0}" == "1" || ! -f "$last_file" ]]; then
    argus_CHANGED_SERVICES="$(argus_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
  else
    local changed=()
    local service current last
    while read -r service current; do
      last="$(argus_read_fingerprint "$service" "$last_file")"
      if [[ -z "$last" || "$current" != "$last" ]]; then
        changed+=("$service")
      fi
    done <"$current_file"
    argus_CHANGED_SERVICES="${changed[*]:-}"
  fi
  export argus_CHANGED_SERVICES

  if [[ "${argus_DEPLOY_FRESH:-0}" == "1" || ! -f "$image_build_file" ]]; then
    # Conservative first-run behavior for the new state file: materialize every app image once.
    argus_IMAGE_STALE_SERVICES="$(argus_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
  else
    local stale=()
    local service current built
    while read -r service current; do
      built="$(argus_read_fingerprint "$service" "$image_build_file")"
      if [[ -z "$built" || "$current" != "$built" ]]; then
        stale+=("$service")
      fi
    done <"$current_file"
    argus_IMAGE_STALE_SERVICES="${stale[*]:-}"
  fi
  export argus_IMAGE_STALE_SERVICES

  argus_RUNTIME_CONFIG_CHANGED=0
  if [[ "${argus_DEPLOY_FRESH:-0}" == "1" || ! -f "$runtime_last" ]]; then
    argus_RUNTIME_CONFIG_CHANGED=1
  elif [[ "$(cat "$runtime_current")" != "$(cat "$runtime_last")" ]]; then
    argus_RUNTIME_CONFIG_CHANGED=1
  fi
  export argus_RUNTIME_CONFIG_CHANGED
}

argus_detect_hot_swap_plan() {
  local source_last source_current image_last image_current
  source_last="$(argus_source_fingerprint_path)"
  source_current="$(argus_current_source_fingerprint_path)"
  image_last="$(argus_image_fingerprint_path)"
  image_current="$(argus_current_image_fingerprint_path)"

  local hot=()
  local image=()
  local service source_now source_then image_now image_then

  # Without split fingerprints from a previous deploy, be conservative and do a normal image build once.
  if [[ ! -f "$source_last" || ! -f "$image_last" ]]; then
    # shellcheck disable=SC2206
    local services=( ${argus_CHANGED_SERVICES:-} )
    argus_HOT_SWAP_SERVICES=""
    argus_IMAGE_REBUILD_SERVICES="${services[*]:-}"
    export argus_HOT_SWAP_SERVICES argus_IMAGE_REBUILD_SERVICES
    return 0
  fi

  # shellcheck disable=SC2206
  local changed_services=( ${argus_CHANGED_SERVICES:-} )
  for service in "${changed_services[@]}"; do
    source_now="$(argus_read_fingerprint "$service" "$source_current")"
    source_then="$(argus_read_fingerprint "$service" "$source_last")"
    image_now="$(argus_read_fingerprint "$service" "$image_current")"
    image_then="$(argus_read_fingerprint "$service" "$image_last")"

    if [[ -z "$image_then" || "$image_now" != "$image_then" ]]; then
      image+=("$service")
    elif [[ -z "$source_then" || "$source_now" != "$source_then" ]]; then
      hot+=("$service")
    else
      image+=("$service")
    fi
  done

  argus_HOT_SWAP_SERVICES="${hot[*]:-}"
  argus_IMAGE_REBUILD_SERVICES="${image[*]:-}"
  export argus_HOT_SWAP_SERVICES argus_IMAGE_REBUILD_SERVICES
}

argus_compose_service_running() {
  local service="$1"
  local cid running
  cid="$(compose ps -q "$service" | tail -n 1 || true)"
  [[ -n "$cid" ]] || return 1
  running="$(argus_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
  [[ "$running" == "true" ]]
}

argus_detect_hot_image_materialization_plan() {
  # Hot-swapped source changes make running containers current, but the named Docker image can
  # remain stale. Rebuild stale images when compose may create/recreate a container from that image.
  # shellcheck disable=SC2206
  local stale_services=( ${argus_IMAGE_STALE_SERVICES:-} )
  # shellcheck disable=SC2206
  local existing_rebuild=( ${argus_IMAGE_REBUILD_SERVICES:-} )
  local materialize=()
  local service

  for service in "${stale_services[@]}"; do
    # Optimization: if the image already exists in Docker and its revision label matches
    # our current BUILD_SOURCE_STAMP, then it's not actually stale, even if the state file is missing.
    local image_name="argus-engine-${service}:local"
    local existing_revision
    existing_revision="$(argus_docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image_name" 2>/dev/null || echo "missing")"

    if [[ "$existing_revision" == "${BUILD_SOURCE_STAMP}" ]]; then
      continue
    fi

    if [[ "${argus_RUNTIME_CONFIG_CHANGED:-0}" == "1" || "${argus_FORCE_RECREATE:-0}" == "1" ]]; then
      materialize+=("$service")
    elif ! argus_compose_service_running "$service"; then
      materialize+=("$service")
    fi
  done

  argus_IMAGE_REBUILD_SERVICES="$(argus_unique_services "${existing_rebuild[@]}" "${materialize[@]}")"

  # A service rebuilt as an image must not also be hot-swapped in the same deploy.
  # shellcheck disable=SC2206
  local hot_services=( ${argus_HOT_SWAP_SERVICES:-} )
  argus_HOT_SWAP_SERVICES="$(argus_subtract_services "$argus_IMAGE_REBUILD_SERVICES" "${hot_services[@]}")"

  export argus_IMAGE_REBUILD_SERVICES argus_HOT_SWAP_SERVICES
}

argus_replace_state_file() {
  local src="$1"
  local dest="$2"
  if [[ -d "$dest" && ! -L "$dest" ]]; then
    rm -rf "$dest"
  fi
  mv -f "$src" "$dest"
}

argus_update_image_build_fingerprints() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  local current_file image_build_file tmp_file normalized_file service current
  current_file="$(argus_current_fingerprint_path)"
  image_build_file="$(argus_image_build_fingerprint_path)"
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
    current="$(argus_read_fingerprint "$service" "$current_file")"
    [[ -n "$current" ]] || continue
    awk -v svc="$service" '$1 != svc { print }' "$tmp_file" >"${tmp_file}.next"
    mv -f "${tmp_file}.next" "$tmp_file"
    printf '%s %s\n' "$service" "$current" >>"$tmp_file"
  done

  : >"$normalized_file"
  while IFS= read -r service; do
    current="$(argus_read_fingerprint "$service" "$tmp_file")"
    [[ -n "$current" ]] && printf '%s %s\n' "$service" "$current" >>"$normalized_file"
  done < <(argus_all_dotnet_services)

  argus_replace_state_file "$normalized_file" "$image_build_file"
  rm -f "$tmp_file"
}

argus_record_built_service_fingerprints() {
  # shellcheck disable=SC2206
  local services=( ${argus_BUILT_SERVICES:-} )
  [[ ${#services[@]} -gt 0 ]] || return 0
  argus_update_image_build_fingerprints "${services[@]}"
}

argus_commit_current_fingerprints() {
  local current_file last_file current_source last_source current_image last_image current_runtime last_runtime
  current_file="$(argus_current_fingerprint_path)"
  last_file="$(argus_fingerprint_path)"
  current_source="$(argus_current_source_fingerprint_path)"
  last_source="$(argus_source_fingerprint_path)"
  current_image="$(argus_current_image_fingerprint_path)"
  last_image="$(argus_image_fingerprint_path)"
  current_runtime="$(argus_current_runtime_fingerprint_path)"
  last_runtime="$(argus_runtime_fingerprint_path)"

  [[ -f "$current_file" ]] && argus_replace_state_file "$current_file" "$last_file"
  [[ -f "$current_source" ]] && argus_replace_state_file "$current_source" "$last_source"
  [[ -f "$current_image" ]] && argus_replace_state_file "$current_image" "$last_image"
  [[ -f "$current_runtime" ]] && argus_replace_state_file "$current_runtime" "$last_runtime"
}

# Hash of deploy recipes retained for image labels only. It no longer controls whether every service rebuilds.
argus_recipe_bundle_hash() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1
  argus_sha256_file_list "$root" \
    deploy/docker-compose.yml \
    deploy/Dockerfile.web \
    deploy/Dockerfile.worker \
    deploy/Dockerfile.worker-enum \
    deploy/Dockerfile.base-runtime \
    deploy/Dockerfile.base-recon \
    deploy/wordlists \
    deploy/artifacts/recon-tools
}

argus_export_build_stamp() {
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
  recipe="$(argus_recipe_bundle_hash "$root")"
  export BUILD_SOURCE_STAMP="${BUILD_SOURCE_STAMP}+${recipe:0:16}"
  echo "BUILD_SOURCE_STAMP=${BUILD_SOURCE_STAMP}"
}

argus_export_component_versions() {
  local root="${1:-}"
  [[ -n "$root" ]] || return 1
  local service version var_name project_path
  while IFS= read -r service; do
    project_path="$(argus_service_csproj "$service")"
    version="$(grep -oPm1 '(?<=<Version>)[^<]+' "$root/$project_path" 2>/dev/null || echo '2.0.0')"
    var_name="VERSION_${service//-/_}"
    var_name="${var_name^^}"
    export "$var_name=$version"
  done < <(argus_all_dotnet_services)
}

argus_last_deploy_stamp_path() {
  : "${ROOT:?ROOT must point to DotNetSolution root}"
  echo "$ROOT/deploy/.last-deploy-stamp"
}

argus_write_last_deploy_stamp() {
  local p
  p="$(argus_last_deploy_stamp_path)"
  printf '%s\n' "${BUILD_SOURCE_STAMP}" >"$p.tmp"
  mv -f "$p.tmp" "$p"
}

argus_decide_incremental_deploy() {
  unset argus_DEPLOY_SKIP_BUILD
  unset argus_HOT_SWAP_SERVICES
  unset argus_IMAGE_REBUILD_SERVICES
  unset argus_BUILT_SERVICES
  argus_DEPLOY_MODE="${argus_DEPLOY_MODE:-image}"

  case "$argus_DEPLOY_MODE" in
    image | hot) ;;
    *)
      echo "Invalid argus_DEPLOY_MODE='$argus_DEPLOY_MODE' (expected image or hot)." >&2
      exit 1
      ;;
  esac

  if [[ "${argus_DEPLOY_FRESH:-0}" == "1" ]]; then
    export argus_NO_CACHE=1
    export argus_DEPLOY_MODE=image
    argus_detect_changed_services "$ROOT"
    argus_IMAGE_REBUILD_SERVICES="$(argus_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
    export argus_IMAGE_REBUILD_SERVICES
    echo "Fresh deploy: rebuilding all service images with --no-cache."
    return 0
  fi

  argus_detect_changed_services "$ROOT"

  if [[ "$argus_DEPLOY_MODE" == "hot" ]]; then
    argus_detect_hot_swap_plan
    argus_detect_hot_image_materialization_plan

    if [[ -z "${argus_CHANGED_SERVICES:-}" && -z "${argus_IMAGE_REBUILD_SERVICES:-}" ]]; then
      export argus_DEPLOY_SKIP_BUILD=1
      echo "Hot deploy: no unapplied service source or image recipe changes; skipping image build."
      if [[ "${argus_RUNTIME_CONFIG_CHANGED:-0}" == "1" ]]; then
        echo "  Runtime compose configuration changed; docker compose up will apply it."
      elif [[ -n "${argus_IMAGE_STALE_SERVICES:-}" ]]; then
        echo "  Stale images are allowed because their hot-swapped containers are already running."
      fi
      echo "  (Use ./deploy/deploy.sh -fresh to force a full rebuild.)"
      return 0
    fi

    echo "Hot deploy plan:"
    if [[ -n "${argus_HOT_SWAP_SERVICES:-}" ]]; then
      echo "  hot-swap service(s): ${argus_HOT_SWAP_SERVICES}"
    fi
    if [[ -n "${argus_IMAGE_REBUILD_SERVICES:-}" ]]; then
      echo "  image rebuild service(s): ${argus_IMAGE_REBUILD_SERVICES}"
    fi
    if [[ "${argus_RUNTIME_CONFIG_CHANGED:-0}" == "1" ]]; then
      echo "  runtime config changed: compose up will be run after safe image materialization."
    fi
    return 0
  fi

  argus_IMAGE_REBUILD_SERVICES="${argus_IMAGE_STALE_SERVICES:-}"
  export argus_IMAGE_REBUILD_SERVICES

  if [[ -z "${argus_IMAGE_REBUILD_SERVICES:-}" ]]; then
    export argus_DEPLOY_SKIP_BUILD=1
    if [[ -z "${argus_CHANGED_SERVICES:-}" ]]; then
      echo "Fast deploy: no service source or image recipe fingerprints changed; skipping docker compose build."
    else
      echo "Fast deploy: images already match changed service fingerprint(s): ${argus_CHANGED_SERVICES}; skipping docker compose build."
    fi
    if [[ "${argus_RUNTIME_CONFIG_CHANGED:-0}" == "1" ]]; then
      echo "  Runtime compose configuration changed; docker compose up will apply it."
    fi
    echo "  (Use ./deploy/deploy.sh -fresh to force a full rebuild.)"
  else
    if [[ -z "${argus_CHANGED_SERVICES:-}" ]]; then
      echo "Fast deploy: materializing image(s) from previous hot deploy or missing image-build state: ${argus_IMAGE_REBUILD_SERVICES}"
    else
      echo "Fast deploy: rebuilding changed/stale service image(s): ${argus_IMAGE_REBUILD_SERVICES}"
    fi
  fi
}

argus_maybe_git_pull() {
  local root="${1:-}"
  [[ "${argus_GIT_PULL:-}" == "1" ]] || return 0
  [[ -d "$root/.git" ]] || { echo "argus_GIT_PULL=1 but $root has no .git; skipping pull." >&2; return 0; }
  echo "argus_GIT_PULL=1: git pull --ff-only in $root"
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
  export BUILDKIT_PROGRESS="${argus_BUILD_PROGRESS:-auto}"
  local cf="$ROOT/deploy/docker-compose.yml"
  if argus_docker compose version >/dev/null 2>&1; then
    argus_docker compose -f "$cf" "$@"
  elif command -v docker-compose >/dev/null 2>&1; then
    if [[ "${argus_DOCKER_USE_SUDO:-}" == "1" ]]; then
      sudo docker-compose -f "$cf" "$@"
    else
      docker-compose -f "$cf" "$@"
    fi
  else
    echo "Docker Compose is not available (need 'docker compose' or docker-compose)." >&2
    exit 1
  fi
}

argus_run_with_timeout() {
  local timeout_min="${argus_BUILD_TIMEOUT_MIN:-0}"
  if [[ "$timeout_min" =~ ^[0-9]+$ ]] && [[ "$timeout_min" -gt 0 ]]; then
    if command -v timeout >/dev/null 2>&1; then
      timeout "${timeout_min}m" "$@"
      return $?
    fi
    echo "WARN: argus_BUILD_TIMEOUT_MIN is set but 'timeout' is unavailable; continuing without timeout." >&2
  fi

  "$@"
}

argus_note_built_services() {
  local built=("$@")
  # shellcheck disable=SC2206
  local previous=( ${argus_BUILT_SERVICES:-} )
  argus_BUILT_SERVICES="$(argus_unique_services "${previous[@]}" "${built[@]}")"
  export argus_BUILT_SERVICES
}

argus_ensure_base_images() {
  if [[ "${argus_DEPLOY_SKIP_BUILD:-0}" == "1" ]]; then
    return 0
  fi
  if ! argus_docker image inspect argus-engine-base:local >/dev/null 2>&1; then
    echo "Argus runtime base image (argus-engine-base:local) is missing."
    if [[ -f "$ROOT/deploy/load-vendored-images.sh" ]]; then
      bash "$ROOT/deploy/load-vendored-images.sh"
    fi
  fi
  if ! argus_docker image inspect argus-engine-base:local >/dev/null 2>&1; then
    echo "Building runtime base image now to satisfy dependencies..."
    argus_docker build -t argus-engine-base:local -f "$ROOT/deploy/Dockerfile.base-runtime" "$ROOT/deploy/"
  fi
}

argus_compose_build_service_list() {
  argus_ensure_base_images
  local args=(build)
  local built_services=("$@")
  [[ "${argus_PULL_IMAGES:-0}" == "1" || "${argus_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--pull)
  [[ "${argus_NO_CACHE:-}" == "1" ]] && args+=(--no-cache)
  if [[ $# -gt 0 ]]; then
    args+=("$@")
  else
    mapfile -t built_services < <(argus_all_dotnet_services)
  fi
  if [[ "${argus_BUILD_SEQUENTIAL:-0}" == "1" ]]; then
    local build_flags=()
    [[ "${argus_PULL_IMAGES:-0}" == "1" || "${argus_DEPLOY_FRESH:-0}" == "1" ]] && build_flags+=(--pull)
    [[ "${argus_NO_CACHE:-}" == "1" ]] && build_flags+=(--no-cache)
    local svc
    for svc in "${built_services[@]}"; do
      echo "Building service: $svc"
      argus_run_with_timeout compose build "${build_flags[@]}" "$svc"
      argus_note_built_services "$svc"
    done
  else
    argus_run_with_timeout compose "${args[@]}"
    argus_note_built_services "${built_services[@]}"
  fi
}

argus_compose_build() {
  local selected_services="${argus_IMAGE_REBUILD_SERVICES:-${argus_CHANGED_SERVICES:-}}"
  if [[ -n "$selected_services" ]]; then
    # shellcheck disable=SC2206
    local services=( ${selected_services} )
    argus_compose_build_service_list "${services[@]}"
  else
    argus_compose_build_service_list
  fi
}

argus_compose_up_redeploy() {
  local args=(up -d --remove-orphans)
  [[ "${argus_FORCE_RECREATE:-0}" == "1" || "${argus_DEPLOY_FRESH:-0}" == "1" ]] && args+=(--force-recreate)

  # Plain Docker Compose ignores deploy.replicas unless compatibility mode is used.
  # Keep local/EC2 scaling explicit so deployments start enough consumers by default.
  if [[ "${argus_ECS_WORKERS:-0}" == "1" ]]; then
    args+=(
      --scale worker-enum=0
      --scale worker-spider=0
      --scale worker-portscan=0
      --scale worker-highvalue=0
      --scale worker-techid=0
      --scale worker-http-requester=0
    )
  else
    args+=(
      --scale "worker-enum=${argus_ENUM_REPLICAS:-10}"
      --scale "worker-spider=${argus_SPIDER_REPLICAS:-10}"
    )
  fi

  compose "${args[@]}"
}

argus_compose_force_recreate_services() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  if [[ "${argus_ECS_WORKERS:-0}" == "1" ]]; then
    local filtered=()
    local candidate
    for candidate in "${services[@]}"; do
      case "$candidate" in
        worker-spider | worker-enum | worker-portscan | worker-highvalue | worker-techid | worker-http-requester)
          ;;
        *)
          filtered+=("$candidate")
          ;;
      esac
    done
    services=("${filtered[@]}")
    [[ ${#services[@]} -gt 0 ]] || return 0
  fi

  local args=(up -d --no-deps --force-recreate)
  local service include_enum=0 include_spider=0

  for service in "${services[@]}"; do
    case "$service" in
      worker-enum) include_enum=1 ;;
      worker-spider) include_spider=1 ;;
    esac
  done

  # Keep explicit replica counts when a scaled service is recreated from a rebuilt image.
  if [[ "${argus_ECS_WORKERS:-0}" == "1" ]]; then
    [[ "$include_enum" == "1" ]] && args+=(--scale worker-enum=0)
    [[ "$include_spider" == "1" ]] && args+=(--scale worker-spider=0)
  else
    [[ "$include_enum" == "1" ]] && args+=(--scale "worker-enum=${argus_ENUM_REPLICAS:-10}")
    [[ "$include_spider" == "1" ]] && args+=(--scale "worker-spider=${argus_SPIDER_REPLICAS:-10}")
  fi

  args+=("${services[@]}")
  echo "Forcing recreated container(s) from current rebuilt image(s): ${services[*]}"
  compose "${args[@]}"
}

argus_hot_copy_publish_output_to_container() {
  local service="$1"
  local cid="$2"
  local out_abs="$3"
  local temp_dir="/tmp/argus-hot-publish-${service}"

  # Copy into a temporary directory first, then replace /app so removed static assets/Razor bundles
  # do not remain from the previous publish output.
  argus_docker exec "$cid" sh -lc "rm -rf '$temp_dir' && mkdir -p '$temp_dir'"
  argus_docker cp "$out_abs/." "$cid:$temp_dir/"
  argus_docker exec "$cid" sh -lc "find /app -mindepth 1 -maxdepth 1 -exec rm -rf {} + && cp -a '$temp_dir'/. /app/ && rm -rf '$temp_dir'"
}

argus_publish_service_for_hot_swap() {
  local service="$1"
  local csproj out_rel out_abs uid gid
  csproj="$(argus_service_csproj "$service")"
  out_rel="deploy/.hot-publish/$service"
  out_abs="$ROOT/$out_rel"
  uid="$(id -u)"
  gid="$(id -g)"

  rm -rf "$out_abs"
  mkdir -p "$out_abs" "$ROOT/.nuget/packages"

  echo "Publishing $service with cached NuGet packages..."
  argus_docker run --rm \
    --user "$uid:$gid" \
    -v "$ROOT:/workspace" \
    -w /workspace \
    -e DOTNET_CLI_HOME=/tmp/dotnet-cli \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e NUGET_PACKAGES=/workspace/.nuget/packages \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -lc "dotnet restore '$csproj' && dotnet publish '$csproj' -c Release -o '$out_rel' --no-restore /p:UseAppHost=false && if [ '$service' = 'command-center' ]; then test -s '$out_rel/wwwroot/_framework/blazor.web.js' && ! grep -q '^404: Not Found' '$out_rel/wwwroot/_framework/blazor.web.js'; fi"
}

argus_hot_swap_services() {
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
      running="$(argus_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
      if [[ "$running" == "true" ]]; then
        running_cids+=("$cid")
      fi
    done

    if [[ ${#running_cids[@]} -eq 0 ]]; then
      echo "Hot-swap fallback: $service has no running container; it will be rebuilt as an image."
      fallback+=("$service")
      continue
    fi

    argus_publish_service_for_hot_swap "$service"
    out_abs="$ROOT/deploy/.hot-publish/$service"
    echo "Copying publish output into ${#running_cids[@]} running $service container(s), then restarting that service..."
    for cid in "${running_cids[@]}"; do
      argus_hot_copy_publish_output_to_container "$service" "$cid" "$out_abs"
    done
    compose restart "$service"
  done

  argus_HOT_SWAP_FALLBACK_SERVICES="${fallback[*]:-}"
  export argus_HOT_SWAP_FALLBACK_SERVICES
}

argus_compose_image_deploy_for_services() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0
  argus_compose_build_service_list "${services[@]}"
  argus_compose_up_redeploy
  argus_compose_force_recreate_services "${services[@]}"
}

argus_compose_hot_deploy() {
  # shellcheck disable=SC2206
  local image_services=( ${argus_IMAGE_REBUILD_SERVICES:-} )
  # shellcheck disable=SC2206
  local hot_services=( ${argus_HOT_SWAP_SERVICES:-} )
  local compose_up_ran=0

  if [[ ${#image_services[@]} -gt 0 ]]; then
    argus_compose_image_deploy_for_services "${image_services[@]}"
    compose_up_ran=1
  fi

  if [[ ${#hot_services[@]} -gt 0 ]]; then
    argus_hot_swap_services "${hot_services[@]}"
    # shellcheck disable=SC2206
    local fallback_services=( ${argus_HOT_SWAP_FALLBACK_SERVICES:-} )
    if [[ ${#fallback_services[@]} -gt 0 ]]; then
      argus_compose_image_deploy_for_services "${fallback_services[@]}"
      compose_up_ran=1
    fi
  fi

  if [[ "${argus_RUNTIME_CONFIG_CHANGED:-0}" == "1" && "$compose_up_ran" != "1" ]]; then
    argus_compose_up_redeploy
  fi
}

argus_compose_deploy_all() {
  if [[ "${argus_DEPLOY_SKIP_BUILD:-}" == "1" ]]; then
    argus_compose_up_redeploy
  elif [[ "${argus_DEPLOY_MODE:-image}" == "hot" ]]; then
    argus_compose_hot_deploy
  else
    argus_compose_build
    argus_compose_up_redeploy
    # docker compose up does not always recreate a running container when the image tag is stable
    # (for example argus-v2/command-center:local). Force only rebuilt services to use the
    # image that was just produced so website/UI changes are visible after a normal deploy.
    # shellcheck disable=SC2206
    local rebuilt_services=( ${argus_BUILT_SERVICES:-} )
    argus_compose_force_recreate_services "${rebuilt_services[@]}"
  fi
  argus_record_built_service_fingerprints
  argus_commit_current_fingerprints
  argus_write_last_deploy_stamp
}

argus_compose_full_redeploy() {
  argus_compose_deploy_all
}
