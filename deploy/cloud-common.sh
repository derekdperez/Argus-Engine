#!/usr/bin/env bash
# Shared helpers for Argus cloud worker deployment scripts.
# shellcheck shell=bash

argus_cloud_repo_root() {
  argus_cloud_find_repo_root "$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
}

argus_cloud_find_repo_root() {
  local script_dir="${1:-}"
  local candidates=()

  [[ -n "${ARGUS_REPO_ROOT:-}" ]] && candidates+=("$ARGUS_REPO_ROOT")
  candidates+=("$PWD")

  if command -v git >/dev/null 2>&1; then
    local git_root
    git_root="$(git -C "$PWD" rev-parse --show-toplevel 2>/dev/null || true)"
    [[ -n "$git_root" ]] && candidates+=("$git_root")
  fi

  if [[ -n "$script_dir" ]]; then
    local script_root
    script_root="$(cd "$script_dir/../.." 2>/dev/null && pwd -P || true)"
    [[ -n "$script_root" ]] && candidates+=("$script_root")
  fi

  local c
  for c in "${candidates[@]}"; do
    [[ -z "$c" ]] && continue
    if [[ -f "$c/ArgusEngine.slnx" && -d "$c/src" ]]; then
      cd "$c" && pwd -P
      return 0
    fi
  done

  cat >&2 <<'EOF'
Could not locate the Argus Engine repo root.

Run this command from the project root, or set ARGUS_REPO_ROOT explicitly:

  cd /path/to/argus-engine
  ARGUS_REPO_ROOT="$PWD" ./argus-multicloud-deploy-scripts/deploy/azure/build-push-acr.sh
EOF
  exit 2
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

argus_cloud_is_interactive() {
  [[ -t 0 && "${ARGUS_CLOUD_NONINTERACTIVE:-}" != "1" ]]
}

argus_cloud_single_quote() {
  local s="$1"
  s="${s//\'/\'\"\'\"\'}"
  printf "'%s'" "$s"
}

argus_cloud_restore_ownership_if_sudo() {
  local path="$1"
  if [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]] && command -v id >/dev/null 2>&1; then
    local group
    group="$(id -gn "$SUDO_USER" 2>/dev/null || true)"
    if [[ -n "$group" ]]; then
      chown "$SUDO_USER:$group" "$path" 2>/dev/null || true
    else
      chown "$SUDO_USER" "$path" 2>/dev/null || true
    fi
  fi
}

argus_cloud_ensure_config_file() {
  local target="$1"
  local example="${2:-}"
  local description="${3:-configuration file}"

  mkdir -p "$(dirname "$target")"

  if [[ -f "$target" ]]; then
    return 0
  fi

  if [[ -n "$example" && -f "$example" ]]; then
    cp "$example" "$target"
    argus_cloud_restore_ownership_if_sudo "$target"
    echo "Created ${description}: ${target}"
  else
    : > "$target"
    argus_cloud_restore_ownership_if_sudo "$target"
    echo "Created empty ${description}: ${target}"
  fi
}

argus_cloud_upsert_env_value() {
  local env_file="$1"
  local key="$2"
  local value="$3"
  local quoted tmp

  mkdir -p "$(dirname "$env_file")"
  [[ -f "$env_file" ]] || : > "$env_file"

  quoted="$(argus_cloud_single_quote "$value")"
  tmp="$(mktemp)"

  awk -v key="$key" -v line="${key}=${quoted}" '
    BEGIN { done=0 }
    $0 ~ "^[[:space:]]*(export[[:space:]]+)?" key "=" {
      if (!done) {
        print line
        done=1
      }
      next
    }
    { print }
    END {
      if (!done) {
        print line
      }
    }
  ' "$env_file" > "$tmp"

  mv "$tmp" "$env_file"
  argus_cloud_restore_ownership_if_sudo "$env_file"
}

argus_cloud_value_needs_prompt() {
  local v="${1:-}"
  [[ -z "$v" ]] && return 0
  case "$v" in
    *CHANGE_ME*|*REPLACE_ME*|*replace-with*|*replace_me*|*replace*|'< '*|'<'*'>'|argusengineacrreplace)
      return 0
      ;;
  esac
  return 1
}

argus_cloud_prompt_env_var() {
  local env_file="$1"
  local key="$2"
  local prompt="$3"
  local default_value="${4:-}"
  local required="${5:-1}"
  local current="${!key:-}"

  if ! argus_cloud_value_needs_prompt "$current"; then
    return 0
  fi

  if ! argus_cloud_is_interactive; then
    if [[ "$required" == "1" ]]; then
      echo "Missing required setting ${key}. Add it to ${env_file} or run interactively." >&2
      exit 2
    fi
    return 0
  fi

  local value=""
  while true; do
    if [[ -n "$default_value" ]]; then
      read -r -p "${prompt} [${default_value}]: " value
      value="${value:-$default_value}"
    else
      read -r -p "${prompt}: " value
    fi

    if [[ "$required" != "1" || -n "$value" ]]; then
      break
    fi
    echo "${key} is required."
  done

  printf -v "$key" '%s' "$value"
  export "$key"
  argus_cloud_upsert_env_value "$env_file" "$key" "$value"
}

argus_cloud_require_env_vars() {
  local env_file="$1"
  shift

  local key
  for key in "$@"; do
    if argus_cloud_value_needs_prompt "${!key:-}"; then
      echo "Missing required setting ${key}. Add it to ${env_file}." >&2
      exit 2
    fi
  done
}

argus_cloud_default_acr_name() {
  local suffix
  suffix="$(date +%s)"
  printf 'argusengine%s' "$suffix"
}

argus_cloud_guess_azure_subscription_id() {
  if command -v az >/dev/null 2>&1 && az account show >/dev/null 2>&1; then
    az account show --query id -o tsv 2>/dev/null || true
  fi
}

argus_cloud_prompt_azure_env() {
  local env_file="$1"

  local default_subscription
  default_subscription="$(argus_cloud_guess_azure_subscription_id)"

  argus_cloud_prompt_env_var "$env_file" AZURE_SUBSCRIPTION_ID "Azure subscription ID (blank uses current az default)" "$default_subscription" 0
  argus_cloud_prompt_env_var "$env_file" AZURE_LOCATION "Azure region/location" "${AZURE_LOCATION:-eastus}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_RESOURCE_GROUP "Azure resource group name" "${AZURE_RESOURCE_GROUP:-argus-engine-rg}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_CONTAINERAPPS_ENV "Azure Container Apps environment name" "${AZURE_CONTAINERAPPS_ENV:-argus-engine-env}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_ACR_NAME "Azure Container Registry name, lowercase letters/numbers only, globally unique" "${AZURE_ACR_NAME:-$(argus_cloud_default_acr_name)}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_ACR_SKU "Azure Container Registry SKU" "${AZURE_ACR_SKU:-Basic}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_IMAGE_PREFIX "Container image prefix/path" "${AZURE_IMAGE_PREFIX:-argus-engine}" 1
  argus_cloud_prompt_env_var "$env_file" IMAGE_TAG "Container image tag" "${IMAGE_TAG:-latest}" 1
  argus_cloud_prompt_env_var "$env_file" SERVICE_ENV_FILE "Runtime service environment file" "${SERVICE_ENV_FILE:-deploy/azure/service-env}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_MIN_REPLICAS "Default minimum replicas per worker" "${AZURE_MIN_REPLICAS:-1}" 1
  argus_cloud_prompt_env_var "$env_file" AZURE_MAX_REPLICAS "Default maximum replicas per worker" "${AZURE_MAX_REPLICAS:-3}" 1
}

argus_cloud_azure_ensure_login_and_subscription() {
  argus_cloud_require_command az

  if ! az account show >/dev/null 2>&1; then
    if argus_cloud_is_interactive; then
      echo "Azure CLI is not logged in. Starting device-code login..."
      az login --use-device-code >/dev/null
    else
      echo "Azure CLI is not logged in. Run: az login --use-device-code" >&2
      exit 2
    fi
  fi

  if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
    az account set --subscription "$AZURE_SUBSCRIPTION_ID"
  fi
}

argus_cloud_guess_gcp_project_id() {
  if command -v gcloud >/dev/null 2>&1; then
    gcloud config get-value project 2>/dev/null || true
  fi
}

argus_cloud_prompt_gcp_env() {
  local env_file="$1"
  local default_project
  default_project="$(argus_cloud_guess_gcp_project_id)"

  argus_cloud_prompt_env_var "$env_file" GCP_PROJECT_ID "Google Cloud project ID" "${GCP_PROJECT_ID:-$default_project}" 1
  argus_cloud_prompt_env_var "$env_file" GCP_REGION "Google Cloud region" "${GCP_REGION:-us-central1}" 1
  argus_cloud_prompt_env_var "$env_file" GCP_ARTIFACT_REPOSITORY "Artifact Registry Docker repository name" "${GCP_ARTIFACT_REPOSITORY:-argus-engine}" 1
  argus_cloud_prompt_env_var "$env_file" GCP_IMAGE_PREFIX "Container image prefix/path" "${GCP_IMAGE_PREFIX:-argus-engine}" 1
  argus_cloud_prompt_env_var "$env_file" IMAGE_TAG "Container image tag" "${IMAGE_TAG:-latest}" 1
  argus_cloud_prompt_env_var "$env_file" SERVICE_ENV_FILE "Runtime service environment file" "${SERVICE_ENV_FILE:-deploy/gcp/service-env}" 1
  argus_cloud_prompt_env_var "$env_file" GCP_WORKER_INSTANCES "Default Cloud Run Worker Pool instances" "${GCP_WORKER_INSTANCES:-2}" 1
}

argus_cloud_gcp_ensure_login_and_project() {
  argus_cloud_require_command gcloud

  local active_account
  active_account="$(gcloud auth list --filter=status:ACTIVE --format='value(account)' 2>/dev/null | head -n 1 || true)"
  if [[ -z "$active_account" ]]; then
    if argus_cloud_is_interactive; then
      echo "gcloud is not logged in. Starting console-only login..."
      gcloud auth login --no-launch-browser
    else
      echo "gcloud is not logged in. Run: gcloud auth login --no-launch-browser" >&2
      exit 2
    fi
  fi

  gcloud config set project "$GCP_PROJECT_ID" >/dev/null
}

argus_cloud_abs_path_from_repo() {
  local repo_root="$1"
  local path="$2"
  if [[ "$path" == /* ]]; then
    printf '%s' "$path"
  else
    printf '%s/%s' "$repo_root" "$path"
  fi
}

argus_cloud_file_has_placeholders() {
  local file="$1"
  grep -Eq 'CHANGE_ME|10\.0\.0\.10|replace-with|REPLACE_ME|<[^>]+>' "$file" 2>/dev/null
}

argus_cloud_guess_runtime_host() {
  local candidates=(
    "${ARGUS_CLOUD_RUNTIME_HOST:-}"
    "${GCP_CORE_HOST:-}"
    "${CORE_HOST:-}"
    "${ARGUS_CORE_HOST:-}"
    "${EC2_WORKER_CORE_HOST:-}"
  )
  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -n "$candidate" && "$candidate" != *CHANGE_ME* && "$candidate" != *replace* && "$candidate" != 10.0.0.10 ]]; then
      printf '%s' "$candidate"
      return 0
    fi
  done

  if command -v hostname >/dev/null 2>&1; then
    local ips ip
    ips="$(hostname -I 2>/dev/null || true)"
    for ip in $ips; do
      if [[ "$ip" != 127.* && "$ip" != 169.254.* ]]; then
        printf '%s' "$ip"
        return 0
      fi
    done
  fi

  printf '%s' "10.0.0.10"
}

argus_cloud_generate_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32
    return 0
  fi
  tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32
}

argus_cloud_autofill_service_env_passwords() {
  local target="$1"
  local runtime_host
  runtime_host="$(argus_cloud_guess_runtime_host)"
  local postgres_host="${ARGUS_CLOUD_POSTGRES_HOST:-${ARGUS_POSTGRES_HOST:-$runtime_host}}"
  local redis_host="${ARGUS_CLOUD_REDIS_HOST:-${ARGUS_REDIS_HOST:-$runtime_host}}"
  local rabbit_host="${ARGUS_CLOUD_RABBITMQ_HOST:-${ARGUS_RABBITMQ_HOST:-$runtime_host}}"
  local rabbit_management_url="${ARGUS_CLOUD_RABBITMQ_MANAGEMENT_URL:-}"
  local db_password="${ARGUS_CLOUD_DB_PASSWORD:-${ARGUS_DB_PASSWORD:-argus}}"
  local rabbit_password="${ARGUS_CLOUD_RABBITMQ_PASSWORD:-${ARGUS_RABBITMQ_PASSWORD:-argus}}"
  local diagnostics_key="${ARGUS_DIAGNOSTICS_API_KEY:-}"
  local tmp changed
  changed=0

  if [[ -z "$rabbit_management_url" && -n "$rabbit_host" ]]; then
    rabbit_management_url="http://${rabbit_host}:15672"
  fi

  if [[ -z "$diagnostics_key" || "$diagnostics_key" == *CHANGE_ME* || "$diagnostics_key" == *replace* ]]; then
    diagnostics_key="$(argus_cloud_generate_secret)"
  fi

  tmp="$(mktemp)"
  awk \
    -v postgres_host="$postgres_host" \
    -v redis_host="$redis_host" \
    -v rabbit_host="$rabbit_host" \
    -v rabbit_management_url="$rabbit_management_url" \
    -v db_password="$db_password" \
    -v rabbit_password="$rabbit_password" \
    -v diagnostics_key="$diagnostics_key" \
    '
      {
        line=$0
        if ($0 ~ /^ConnectionStrings__Postgres=/) {
          gsub(/Host=10\.0\.0\.10/, "Host=" postgres_host, line)
          gsub(/Password=CHANGE_ME/, "Password=" db_password, line)
        } else if ($0 ~ /^ConnectionStrings__FileStore=/) {
          gsub(/Host=10\.0\.0\.10/, "Host=" postgres_host, line)
          gsub(/Password=CHANGE_ME/, "Password=" db_password, line)
        } else if ($0 ~ /^ConnectionStrings__Redis=/) {
          gsub(/10\.0\.0\.10/, redis_host, line)
        } else if ($0 ~ /^RabbitMq__Host=/) {
          sub(/=.*/, "=" rabbit_host, line)
        } else if ($0 ~ /^RabbitMq__Password=/) {
          sub(/=.*/, "=" rabbit_password, line)
        } else if ($0 ~ /^RabbitMq__ManagementUrl=/) {
          if (rabbit_management_url != "") {
            sub(/=.*/, "=" rabbit_management_url, line)
          } else {
            gsub(/10\.0\.0\.10/, rabbit_host, line)
          }
        } else if ($0 ~ /^Argus__Diagnostics__ApiKey=/) {
          if (line ~ /CHANGE_ME|REPLACE_ME|replace-with|replace_me|replace/) {
            sub(/=.*/, "=" diagnostics_key, line)
          }
        }
        print line
      }
    ' "$target" > "$tmp"

  if ! cmp -s "$target" "$tmp"; then
    mv "$tmp" "$target"
    argus_cloud_restore_ownership_if_sudo "$target"
    changed=1
  else
    rm -f "$tmp"
  fi

  return "$changed"
}

argus_cloud_print_unresolved_placeholders() {
  local target="$1"
  grep -nE 'CHANGE_ME|10\.0\.0\.10|replace-with|REPLACE_ME|<[^>]+>' "$target" 2>/dev/null || true
}

argus_cloud_ensure_service_env() {
  local target="$1"
  local example="${2:-}"
  local label="${3:-cloud}"

  argus_cloud_ensure_config_file "$target" "$example" "${label} runtime service environment file"
  argus_cloud_autofill_service_env_passwords "$target" || true

  if argus_cloud_file_has_placeholders "$target"; then
    cat >&2 <<EOF

${target} still contains example placeholders such as CHANGE_ME or 10.0.0.10.
These values must be endpoints and credentials that ${label} workers can reach at runtime.

Unresolved placeholder lines:
$(argus_cloud_print_unresolved_placeholders "$target")

Set explicit host overrides in your shell or deploy/gcp/.env before rerun:
  ARGUS_CLOUD_RUNTIME_HOST=<reachable host/ip>
  ARGUS_CLOUD_POSTGRES_HOST=<reachable host/ip>
  ARGUS_CLOUD_REDIS_HOST=<reachable host/ip>
  ARGUS_CLOUD_RABBITMQ_HOST=<reachable host/ip>
  ARGUS_CLOUD_RABBITMQ_MANAGEMENT_URL=http://<host>:15672
EOF

    echo "Edit ${target} and rerun." >&2
    exit 2
  fi
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
  # Operation Google Deploy starts with heavier front-door discovery capacity:
  # spider, enum, and HTTP requester get 8 instances; all other worker pools get 2.
  case "$1" in
    worker-spider|worker-http-requester|worker-enum) echo "8" ;;
    *) echo "2" ;;
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
    --build-arg "COMPONENT_VERSION=${ARGUS_ENGINE_VERSION:-2.6.3}"
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
