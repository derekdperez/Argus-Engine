#!/usr/bin/env bash
# Shared helpers for Argus cloud worker deployment scripts.
# shellcheck shell=bash

argus_cloud_repo_root() {
  local script_dir
  script_dir="$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
  cd "$script_dir/../.." && pwd
}

argus_cloud_require_command() {
  local cmd="$1"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 127
  fi
}

argus_cloud_load_env_file() {
  local env_file="$1"
  if [[ -f "$env_file" ]]; then
    # shellcheck source=/dev/null
    set -a
    . "$env_file"
    set +a
  fi
}

argus_cloud_trim() {
  local s="$1"
  s="${s#"${s%%[![:space:]]*}"}"
  s="${s%"${s##*[![:space:]]}"}"
  printf '%s' "$s"
}

argus_cloud_unquote() {
  local s="$1"
  if [[ "$s" == \"*\" && "$s" == *\" ]]; then
    s="${s:1:${#s}-2}"
  elif [[ "$s" == \'*\' && "$s" == *\' ]]; then
    s="${s:1:${#s}-2}"
  fi
  printf '%s' "$s"
}

argus_cloud_env_file_to_azure_args() {
  local env_file="$1"
  local -n out_ref="$2"
  out_ref=()

  if [[ ! -f "$env_file" ]]; then
    echo "Service env file does not exist: $env_file" >&2
    exit 2
  fi

  local raw line key value
  while IFS= read -r raw || [[ -n "$raw" ]]; do
    line="$(argus_cloud_trim "$raw")"
    [[ -z "$line" || "$line" == \#* ]] && continue
    [[ "$line" != *=* ]] && continue
    key="$(argus_cloud_trim "${line%%=*}")"
    value="$(argus_cloud_trim "${line#*=}")"
    value="$(argus_cloud_unquote "$value")"
    out_ref+=("${key}=${value}")
  done < "$env_file"
}

argus_cloud_write_service_env_file() {
  local source_env="$1"
  local service="$2"
  local output="$3"

  if [[ ! -f "$source_env" ]]; then
    echo "Service env file does not exist: $source_env" >&2
    exit 2
  fi

  cp "$source_env" "$output"
  {
    echo ""
    echo "# Added by Argus cloud deployment scripts for ${service}"
    echo "Argus__SkipStartupDatabase=true"
    echo "ARGUS_SKIP_STARTUP_DATABASE=1"
    if [[ "$service" == "worker-spider" || "$service" == "worker-http-requester" ]]; then
      echo "Spider__Http__AllowInsecureSsl=false"
      echo "HttpRequester__AllowInsecureSsl=false"
    fi
  } >> "$output"
}

argus_cloud_default_services() {
  cat <<'EOF'
gatekeeper
command-center-spider-dispatcher
worker-spider
worker-http-requester
worker-enum
worker-portscan
worker-highvalue
worker-techid
EOF
}

argus_cloud_selected_services() {
  if [[ "$#" -gt 0 ]]; then
    printf '%s\n' "$@"
    return 0
  fi

  if [[ -n "${ARGUS_CLOUD_SERVICES:-}" ]]; then
    # shellcheck disable=SC2206
    local services=( ${ARGUS_CLOUD_SERVICES} )
    printf '%s\n' "${services[@]}"
    return 0
  fi

  argus_cloud_default_services
}

argus_cloud_sanitize_env_suffix() {
  local s="$1"
  s="${s//-/_}"
  printf '%s' "${s^^}"
}

argus_cloud_service_project_dir() {
  case "$1" in
    gatekeeper) echo "ArgusEngine.Gatekeeper" ;;
    command-center-spider-dispatcher) echo "ArgusEngine.CommandCenter.SpiderDispatcher" ;;
    worker-spider) echo "ArgusEngine.Workers.Spider" ;;
    worker-http-requester) echo "ArgusEngine.Workers.HttpRequester" ;;
    worker-enum) echo "ArgusEngine.Workers.Enumeration" ;;
    worker-portscan) echo "ArgusEngine.Workers.PortScan" ;;
    worker-highvalue) echo "ArgusEngine.Workers.HighValue" ;;
    worker-techid) echo "ArgusEngine.Workers.TechnologyIdentification" ;;
    *) echo "Unknown Argus cloud worker service: $1" >&2; return 1 ;;
  esac
}

argus_cloud_service_app_dll() {
  case "$1" in
    gatekeeper) echo "ArgusEngine.Gatekeeper.dll" ;;
    command-center-spider-dispatcher) echo "ArgusEngine.CommandCenter.SpiderDispatcher.dll" ;;
    worker-spider) echo "ArgusEngine.Workers.Spider.dll" ;;
    worker-http-requester) echo "ArgusEngine.Workers.HttpRequester.dll" ;;
    worker-enum) echo "ArgusEngine.Workers.Enumeration.dll" ;;
    worker-portscan) echo "ArgusEngine.Workers.PortScan.dll" ;;
    worker-highvalue) echo "ArgusEngine.Workers.HighValue.dll" ;;
    worker-techid) echo "ArgusEngine.Workers.TechnologyIdentification.dll" ;;
    *) echo "Unknown Argus cloud worker service: $1" >&2; return 1 ;;
  esac
}

argus_cloud_service_dockerfile() {
  case "$1" in
    worker-enum) echo "deploy/Dockerfile.worker-enum" ;;
    *) echo "deploy/Dockerfile.worker" ;;
  esac
}

argus_cloud_service_default_instances() {
  case "$1" in
    worker-http-requester) echo "3" ;;
    worker-enum) echo "2" ;;
    *) echo "1" ;;
  esac
}

argus_cloud_service_default_cpu_azure() {
  case "$1" in
    worker-spider|worker-http-requester|worker-enum) echo "1.0" ;;
    *) echo "0.5" ;;
  esac
}

argus_cloud_service_default_memory_azure() {
  case "$1" in
    worker-spider|worker-http-requester|worker-enum) echo "2.0Gi" ;;
    *) echo "1.0Gi" ;;
  esac
}

argus_cloud_service_default_cpu_gcp() {
  case "$1" in
    worker-spider|worker-http-requester|worker-enum) echo "1" ;;
    *) echo "1" ;;
  esac
}

argus_cloud_service_default_memory_gcp() {
  case "$1" in
    worker-spider|worker-http-requester|worker-enum) echo "2Gi" ;;
    *) echo "1Gi" ;;
  esac
}

argus_cloud_service_var_or_default() {
  local prefix="$1"
  local service="$2"
  local default_value="$3"
  local suffix var
  suffix="$(argus_cloud_sanitize_env_suffix "$service")"
  var="${prefix}_${suffix}"
  printf '%s' "${!var:-$default_value}"
}

argus_cloud_build_service_image() {
  local service="$1"
  local image="$2"
  local dockerfile project_dir app_dll
  dockerfile="$(argus_cloud_service_dockerfile "$service")"
  project_dir="$(argus_cloud_service_project_dir "$service")"
  app_dll="$(argus_cloud_service_app_dll "$service")"

  local build_args=(
    -f "$dockerfile"
    --build-arg "PROJECT_DIR=$project_dir"
    --build-arg "APP_DLL=$app_dll"
    --build-arg "BUILD_SOURCE_STAMP=${BUILD_SOURCE_STAMP:-unknown}"
    --build-arg "COMPONENT_VERSION=${ARGUS_ENGINE_VERSION:-2.6.2}"
  )

  if [[ "$service" == "worker-enum" ]]; then
    build_args+=(
      --build-arg "SUBFINDER_VERSION=${SUBFINDER_VERSION:-2.14.0}"
      --build-arg "AMASS_VERSION=${AMASS_VERSION:-5.1.1}"
    )
  fi

  echo "Building ${service}: ${image}"
  docker build "${build_args[@]}" -t "$image" .
}

argus_cloud_build_base_images() {
  if [[ ! -x "deploy/build-base-images.sh" ]]; then
    echo "deploy/build-base-images.sh is missing or not executable; running with bash anyway." >&2
  fi
  bash deploy/build-base-images.sh
}

argus_cloud_export_build_stamp() {
  if [[ -n "${BUILD_SOURCE_STAMP:-}" ]]; then
    return 0
  fi

  if [[ -d .git ]] && command -v git >/dev/null 2>&1; then
    BUILD_SOURCE_STAMP="$(git rev-parse --short=12 HEAD 2>/dev/null || true)"
  fi
  export BUILD_SOURCE_STAMP="${BUILD_SOURCE_STAMP:-manual}"
}
