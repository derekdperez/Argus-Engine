#!/usr/bin/env bash
# Shared helpers for Argus Engine cloud deployment scripts.
# These scripts are intentionally Bash-only so they can run from EC2, local Linux, macOS, or CI.

set -euo pipefail

argus_log() {
  printf '\033[1;34m[ARGUS]\033[0m %s\n' "$*" >&2
}

argus_warn() {
  printf '\033[1;33m[ARGUS WARN]\033[0m %s\n' "$*" >&2
}

argus_die() {
  printf '\033[1;31m[ARGUS ERROR]\033[0m %s\n' "$*" >&2
  exit 1
}

argus_find_repo_root() {
  local start="${1:-$PWD}"
  local dir
  dir="$(cd "$start" 2>/dev/null && pwd)" || return 1

  while [[ "$dir" != "/" ]]; do
    if [[ -f "$dir/ArgusEngine.slnx" && -d "$dir/deploy" ]]; then
      printf '%s\n' "$dir"
      return 0
    fi
    dir="$(dirname "$dir")"
  done

  return 1
}

ARGUS_REPO_ROOT="${ARGUS_REPO_ROOT:-$(argus_find_repo_root "$PWD" || true)}"
if [[ -z "${ARGUS_REPO_ROOT}" ]]; then
  argus_die "Could not find Argus repo root. Run this from the repository root, or set ARGUS_REPO_ROOT=/path/to/argus-engine."
fi

ARGUS_DEPLOY_DIR="$ARGUS_REPO_ROOT/deploy"

argus_require_cmd() {
  local cmd="$1"
  command -v "$cmd" >/dev/null 2>&1 || argus_die "Missing required command: $cmd"
}

argus_warn_if_sudo() {
  if [[ "${EUID}" -eq 0 && -n "${SUDO_USER:-}" ]]; then
    argus_warn "Running as sudo/root. Azure CLI login and Docker config may be stored under root, not ${SUDO_USER}."
  fi
}

argus_default_version() {
  if [[ -n "${ARGUS_ENGINE_VERSION:-}" ]]; then
    printf '%s\n' "$ARGUS_ENGINE_VERSION"
  elif [[ -f "$ARGUS_REPO_ROOT/VERSION" ]]; then
    tr -d '[:space:]' < "$ARGUS_REPO_ROOT/VERSION"
  else
    printf 'latest\n'
  fi
}

argus_env_quote() {
  # Print a shell-safe single-quoted value for .env files.
  local value="${1:-}"
  printf "'%s'" "${value//\'/\'\\\'\'}"
}

argus_env_get() {
  local key="$1"
  local value="${!key-}"
  printf '%s\n' "$value"
}

argus_env_is_placeholder() {
  local value="${1:-}"
  [[ -z "$value" || "$value" == "CHANGE_ME" || "$value" == "<CHANGE_ME>" || "$value" == "changeme" || "$value" == "example" ]]
}

argus_upsert_env() {
  local file="$1"
  local key="$2"
  local value="$3"
  mkdir -p "$(dirname "$file")"
  touch "$file"

  local quoted
  quoted="$(argus_env_quote "$value")"

  if grep -Eq "^${key}=" "$file"; then
    local tmp
    tmp="$(mktemp)"
    awk -v k="$key" -v v="$quoted" 'BEGIN{done=0} $0 ~ "^" k "=" {print k "=" v; done=1; next} {print} END{if(!done) print k "=" v}' "$file" > "$tmp"
    cat "$tmp" > "$file"
    rm -f "$tmp"
  else
    printf '%s=%s\n' "$key" "$quoted" >> "$file"
  fi
}

argus_load_env_file() {
  local file="$1"
  if [[ -f "$file" ]]; then
    # shellcheck disable=SC1090
    set -a
    source "$file"
    set +a
  fi
}

argus_prompt_value() {
  local key="$1"
  local prompt="$2"
  local default="${3:-}"
  local secret="${4:-false}"
  local current="${!key-}"

  if ! argus_env_is_placeholder "$current"; then
    printf '%s\n' "$current"
    return 0
  fi

  local shown_default="$default"
  local answer=""

  if [[ ! -t 0 && ! -r /dev/tty ]]; then
    if [[ -n "$default" ]]; then
      printf '%s\n' "$default"
      return 0
    fi
    argus_die "Missing $key and no interactive terminal is available. Set it in the relevant .env file."
  fi

  while true; do
    if [[ "$secret" == "true" ]]; then
      if [[ -n "$shown_default" ]]; then
        printf '%s [%s]: ' "$prompt" "hidden default" > /dev/tty
      else
        printf '%s: ' "$prompt" > /dev/tty
      fi
      IFS= read -rs answer < /dev/tty || true
      printf '\n' > /dev/tty
    else
      if [[ -n "$shown_default" ]]; then
        printf '%s [%s]: ' "$prompt" "$shown_default" > /dev/tty
      else
        printf '%s: ' "$prompt" > /dev/tty
      fi
      IFS= read -r answer < /dev/tty || true
    fi

    if [[ -z "$answer" ]]; then
      answer="$default"
    fi

    if [[ -n "$answer" ]]; then
      printf '%s\n' "$answer"
      return 0
    fi

    printf 'A value is required.\n' > /dev/tty
  done
}

argus_ensure_gitignored_local_env() {
  local gitignore="$ARGUS_REPO_ROOT/.gitignore"
  touch "$gitignore"
  grep -qxF 'deploy/azure/.env' "$gitignore" || printf '\ndeploy/azure/.env\n' >> "$gitignore"
  grep -qxF 'deploy/azure/service-env' "$gitignore" || printf 'deploy/azure/service-env\n' >> "$gitignore"
}

argus_parse_env_keys() {
  local file="$1"
  [[ -f "$file" ]] || return 0
  grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$file" | cut -d= -f1 | sort -u
}

argus_make_azure_acr_name_default() {
  local raw
  raw="$(basename "$ARGUS_REPO_ROOT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"
  raw="${raw:-argusengine}"
  # ACR name: alphanumeric, globally unique, 5-50 chars. Add a stable-ish suffix from path hash.
  local suffix
  suffix="$(printf '%s' "$ARGUS_REPO_ROOT" | cksum | awk '{print $1}')"
  printf '%s%s' "${raw:0:32}" "${suffix:0:8}"
}

argus_azure_env_file() {
  printf '%s\n' "$ARGUS_REPO_ROOT/deploy/azure/.env"
}

argus_azure_service_env_file() {
  local configured="${SERVICE_ENV_FILE:-deploy/azure/service-env}"
  if [[ "$configured" = /* ]]; then
    printf '%s
' "$configured"
  else
    printf '%s
' "$ARGUS_REPO_ROOT/$configured"
  fi
}

argus_azure_bootstrap_env() {
  local env_file
  env_file="$(argus_azure_env_file)"
  mkdir -p "$(dirname "$env_file")"

  if [[ ! -f "$env_file" ]]; then
    if [[ -f "$ARGUS_REPO_ROOT/deploy/azure/.env.example" ]]; then
      cp "$ARGUS_REPO_ROOT/deploy/azure/.env.example" "$env_file"
    else
      touch "$env_file"
    fi
    argus_log "Created $env_file"
  fi

  argus_load_env_file "$env_file"

  local subscription default_rg default_env default_acr default_tag location default_sku default_prefix default_service_env default_min default_max default_cpu default_memory
  default_rg="${AZURE_RESOURCE_GROUP:-argus-engine-rg}"
  default_env="${AZURE_CONTAINERAPPS_ENV:-argus-engine-env}"
  default_acr="${AZURE_ACR_NAME:-$(argus_make_azure_acr_name_default)}"
  default_tag="${IMAGE_TAG:-$(argus_default_version)}"
  default_sku="${AZURE_ACR_SKU:-Basic}"
  default_prefix="${AZURE_IMAGE_PREFIX:-argus-engine}"
  default_service_env="${SERVICE_ENV_FILE:-deploy/azure/service-env}"
  default_min="${AZURE_MIN_REPLICAS:-1}"
  default_max="${AZURE_MAX_REPLICAS:-3}"
  default_cpu="${AZURE_CONTAINER_CPU:-0.5}"
  default_memory="${AZURE_CONTAINER_MEMORY:-1.0Gi}"

  subscription="$(argus_prompt_value AZURE_SUBSCRIPTION_ID 'Azure subscription ID or name' "${AZURE_SUBSCRIPTION_ID:-}")"
  location="$(argus_prompt_value AZURE_LOCATION 'Azure region/location' "${AZURE_LOCATION:-eastus}")"
  AZURE_RESOURCE_GROUP="$(argus_prompt_value AZURE_RESOURCE_GROUP 'Azure resource group' "$default_rg")"
  AZURE_CONTAINERAPPS_ENV="$(argus_prompt_value AZURE_CONTAINERAPPS_ENV 'Azure Container Apps environment name' "$default_env")"
  AZURE_ACR_NAME="$(argus_prompt_value AZURE_ACR_NAME 'Azure Container Registry name (globally unique, lowercase letters/numbers)' "$default_acr")"
  AZURE_ACR_SKU="$(argus_prompt_value AZURE_ACR_SKU 'Azure Container Registry SKU' "$default_sku")"
  AZURE_IMAGE_PREFIX="$(argus_prompt_value AZURE_IMAGE_PREFIX 'Azure image repository prefix' "$default_prefix")"
  IMAGE_TAG="$(argus_prompt_value IMAGE_TAG 'Container image tag to build/push' "$default_tag")"
  ARGUS_ENGINE_VERSION="$(argus_prompt_value ARGUS_ENGINE_VERSION 'Argus compose/build version' "$(argus_default_version)")"
  SERVICE_ENV_FILE="$(argus_prompt_value SERVICE_ENV_FILE 'Runtime service env file path' "$default_service_env")"
  AZURE_MIN_REPLICAS="$(argus_prompt_value AZURE_MIN_REPLICAS 'Azure minimum replicas per worker' "$default_min")"
  AZURE_MAX_REPLICAS="$(argus_prompt_value AZURE_MAX_REPLICAS 'Azure maximum replicas per worker' "$default_max")"
  AZURE_CONTAINER_CPU="$(argus_prompt_value AZURE_CONTAINER_CPU 'Azure container CPU per worker' "$default_cpu")"
  AZURE_CONTAINER_MEMORY="$(argus_prompt_value AZURE_CONTAINER_MEMORY 'Azure container memory per worker' "$default_memory")"

  # ACR names must be alphanumeric only. Normalize common mistakes before persisting.
  AZURE_ACR_NAME="$(printf '%s' "$AZURE_ACR_NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"
  if [[ ${#AZURE_ACR_NAME} -lt 5 || ${#AZURE_ACR_NAME} -gt 50 ]]; then
    argus_die "AZURE_ACR_NAME must be 5-50 alphanumeric characters after normalization. Got: $AZURE_ACR_NAME"
  fi

  AZURE_SUBSCRIPTION_ID="$subscription"
  AZURE_LOCATION="$location"

  argus_upsert_env "$env_file" AZURE_SUBSCRIPTION_ID "$AZURE_SUBSCRIPTION_ID"
  argus_upsert_env "$env_file" AZURE_LOCATION "$AZURE_LOCATION"
  argus_upsert_env "$env_file" AZURE_RESOURCE_GROUP "$AZURE_RESOURCE_GROUP"
  argus_upsert_env "$env_file" AZURE_CONTAINERAPPS_ENV "$AZURE_CONTAINERAPPS_ENV"
  argus_upsert_env "$env_file" AZURE_ACR_NAME "$AZURE_ACR_NAME"
  argus_upsert_env "$env_file" AZURE_ACR_SKU "$AZURE_ACR_SKU"
  argus_upsert_env "$env_file" AZURE_IMAGE_PREFIX "$AZURE_IMAGE_PREFIX"
  argus_upsert_env "$env_file" IMAGE_TAG "$IMAGE_TAG"
  argus_upsert_env "$env_file" ARGUS_ENGINE_VERSION "$ARGUS_ENGINE_VERSION"
  argus_upsert_env "$env_file" SERVICE_ENV_FILE "$SERVICE_ENV_FILE"
  argus_upsert_env "$env_file" AZURE_MIN_REPLICAS "$AZURE_MIN_REPLICAS"
  argus_upsert_env "$env_file" AZURE_MAX_REPLICAS "$AZURE_MAX_REPLICAS"
  argus_upsert_env "$env_file" AZURE_CONTAINER_CPU "$AZURE_CONTAINER_CPU"
  argus_upsert_env "$env_file" AZURE_CONTAINER_MEMORY "$AZURE_CONTAINER_MEMORY"

  export AZURE_SUBSCRIPTION_ID AZURE_LOCATION AZURE_RESOURCE_GROUP AZURE_CONTAINERAPPS_ENV AZURE_ACR_NAME AZURE_ACR_SKU AZURE_IMAGE_PREFIX IMAGE_TAG ARGUS_ENGINE_VERSION SERVICE_ENV_FILE AZURE_MIN_REPLICAS AZURE_MAX_REPLICAS AZURE_CONTAINER_CPU AZURE_CONTAINER_MEMORY

  argus_ensure_gitignored_local_env
}

argus_azure_bootstrap_service_env() {
  local service_env
  service_env="$(argus_azure_service_env_file)"
  mkdir -p "$(dirname "$service_env")"

  if [[ ! -f "$service_env" ]]; then
    if [[ -f "$ARGUS_REPO_ROOT/deploy/azure/service-env.example" ]]; then
      cp "$ARGUS_REPO_ROOT/deploy/azure/service-env.example" "$service_env"
    else
      touch "$service_env"
    fi
    argus_log "Created $service_env"
  fi

  argus_load_env_file "$service_env"

  local pg_default redis_default rabbit_host rabbit_user rabbit_pass rabbit_vhost rabbit_mgmt diag_key
  pg_default="${ConnectionStrings__Postgres:-Host=CHANGE_ME;Port=5432;Database=argus_engine;Username=argus;Password=CHANGE_ME}"
  redis_default="${ConnectionStrings__Redis:-CHANGE_ME:6379}"
  rabbit_host="${RabbitMq__Host:-CHANGE_ME}"
  rabbit_user="${RabbitMq__Username:-argus}"
  rabbit_pass="${RabbitMq__Password:-}"
  rabbit_vhost="${RabbitMq__VirtualHost:-/}"
  rabbit_mgmt="${RabbitMq__ManagementUrl:-http://CHANGE_ME:15672}"
  diag_key="${Argus__Diagnostics__ApiKey:-$(printf 'argus-azure-%s' "$(date +%s)")}"

  if [[ "$pg_default" == *"CHANGE_ME"* || "$pg_default" == *"10.0.0.10"* ]]; then
    ConnectionStrings__Postgres="$(argus_prompt_value ConnectionStrings__Postgres 'Postgres connection string reachable from Azure Container Apps' "$pg_default")"
    argus_upsert_env "$service_env" ConnectionStrings__Postgres "$ConnectionStrings__Postgres"
  fi

  if [[ "$redis_default" == *"CHANGE_ME"* || "$redis_default" == *"10.0.0.10"* ]]; then
    ConnectionStrings__Redis="$(argus_prompt_value ConnectionStrings__Redis 'Redis connection string/host reachable from Azure Container Apps' "$redis_default")"
    argus_upsert_env "$service_env" ConnectionStrings__Redis "$ConnectionStrings__Redis"
  fi

  if [[ "$rabbit_host" == "CHANGE_ME" || "$rabbit_host" == "10.0.0.10" ]]; then
    RabbitMq__Host="$(argus_prompt_value RabbitMq__Host 'RabbitMQ host reachable from Azure Container Apps' "$rabbit_host")"
    argus_upsert_env "$service_env" RabbitMq__Host "$RabbitMq__Host"
  fi

  RabbitMq__Username="$(argus_prompt_value RabbitMq__Username 'RabbitMQ username' "$rabbit_user")"
  RabbitMq__Password="$(argus_prompt_value RabbitMq__Password 'RabbitMQ password' "$rabbit_pass" true)"
  RabbitMq__VirtualHost="$(argus_prompt_value RabbitMq__VirtualHost 'RabbitMQ virtual host' "$rabbit_vhost")"

  if [[ "$rabbit_mgmt" == *"CHANGE_ME"* || "$rabbit_mgmt" == *"10.0.0.10"* ]]; then
    RabbitMq__ManagementUrl="$(argus_prompt_value RabbitMq__ManagementUrl 'RabbitMQ management URL reachable from Azure Container Apps' "$rabbit_mgmt")"
    argus_upsert_env "$service_env" RabbitMq__ManagementUrl "$RabbitMq__ManagementUrl"
  fi

  Argus__Diagnostics__ApiKey="$(argus_prompt_value Argus__Diagnostics__ApiKey 'Argus diagnostics API key' "$diag_key" true)"

  argus_upsert_env "$service_env" RabbitMq__Username "$RabbitMq__Username"
  argus_upsert_env "$service_env" RabbitMq__Password "$RabbitMq__Password"
  argus_upsert_env "$service_env" RabbitMq__VirtualHost "$RabbitMq__VirtualHost"
  argus_upsert_env "$service_env" Argus__Diagnostics__ApiKey "$Argus__Diagnostics__ApiKey"

  export ConnectionStrings__Postgres ConnectionStrings__Redis RabbitMq__Host RabbitMq__Username RabbitMq__Password RabbitMq__VirtualHost RabbitMq__ManagementUrl Argus__Diagnostics__ApiKey
  argus_ensure_gitignored_local_env
}

argus_azure_login_if_needed() {
  argus_require_cmd az

  if ! az account show >/dev/null 2>&1; then
    argus_log "Azure CLI is not logged in; starting device-code login."
    az login --use-device-code >/dev/null
  fi

  if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
    az account set --subscription "$AZURE_SUBSCRIPTION_ID"
  fi
}

argus_azure_ensure_containerapp_extension() {
  argus_require_cmd az
  if ! az containerapp --help >/dev/null 2>&1; then
    argus_log "Installing/upgrading Azure Container Apps CLI extension."
    az extension add --name containerapp --upgrade >/dev/null
  fi
}

argus_azure_ensure_resources() {
  argus_azure_login_if_needed
  argus_azure_ensure_containerapp_extension

  argus_log "Ensuring resource group: $AZURE_RESOURCE_GROUP ($AZURE_LOCATION)"
  az group create \
    --name "$AZURE_RESOURCE_GROUP" \
    --location "$AZURE_LOCATION" \
    --output none

  if ! az acr show --resource-group "$AZURE_RESOURCE_GROUP" --name "$AZURE_ACR_NAME" >/dev/null 2>&1; then
    argus_log "Creating Azure Container Registry: $AZURE_ACR_NAME"
    az acr create \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_ACR_NAME" \
      --sku "${AZURE_ACR_SKU:-Basic}" \
      --admin-enabled true \
      --output none
  else
    argus_log "Azure Container Registry already exists: $AZURE_ACR_NAME"
    az acr update \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_ACR_NAME" \
      --admin-enabled true \
      --output none
  fi

  if ! az containerapp env show --resource-group "$AZURE_RESOURCE_GROUP" --name "$AZURE_CONTAINERAPPS_ENV" >/dev/null 2>&1; then
    argus_log "Creating Azure Container Apps environment: $AZURE_CONTAINERAPPS_ENV"
    az containerapp env create \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_CONTAINERAPPS_ENV" \
      --location "$AZURE_LOCATION" \
      --output none
  else
    argus_log "Azure Container Apps environment already exists: $AZURE_CONTAINERAPPS_ENV"
  fi
}

argus_azure_acr_login_server() {
  az acr show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_ACR_NAME" \
    --query loginServer \
    --output tsv
}

argus_azure_acr_username() {
  az acr credential show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_ACR_NAME" \
    --query username \
    --output tsv
}

argus_azure_acr_password() {
  az acr credential show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_ACR_NAME" \
    --query 'passwords[0].value' \
    --output tsv
}

argus_service_project_dir() {
  case "$1" in
    command-center-gateway) printf 'ArgusEngine.CommandCenter.Gateway\n' ;;
    command-center-operations-api) printf 'ArgusEngine.CommandCenter.Operations.Api\n' ;;
    command-center-discovery-api) printf 'ArgusEngine.CommandCenter.Discovery.Api\n' ;;
    command-center-worker-control-api) printf 'ArgusEngine.CommandCenter.WorkerControl.Api\n' ;;
    command-center-maintenance-api) printf 'ArgusEngine.CommandCenter.Maintenance.Api\n' ;;
    command-center-updates-api) printf 'ArgusEngine.CommandCenter.Updates.Api\n' ;;
    command-center-realtime) printf 'ArgusEngine.CommandCenter.Realtime.Host\n' ;;
    command-center-web) printf 'ArgusEngine.CommandCenter.Web\n' ;;
    command-center-bootstrapper) printf 'ArgusEngine.CommandCenter.Bootstrapper\n' ;;
    command-center-spider-dispatcher) printf 'ArgusEngine.CommandCenter.SpiderDispatcher\n' ;;
    gatekeeper) printf 'ArgusEngine.Gatekeeper\n' ;;
    worker-spider) printf 'ArgusEngine.Workers.Spider\n' ;;
    worker-http-requester) printf 'ArgusEngine.Workers.HttpRequester\n' ;;
    worker-enum) printf 'ArgusEngine.Workers.Enumeration\n' ;;
    worker-portscan) printf 'ArgusEngine.Workers.PortScan\n' ;;
    worker-highvalue) printf 'ArgusEngine.Workers.HighValue\n' ;;
    worker-techid) printf 'ArgusEngine.Workers.TechnologyIdentification\n' ;;
    *) return 1 ;;
  esac
}

argus_known_services() {
  cat <<'SERVICES'
command-center-gateway
command-center-operations-api
command-center-discovery-api
command-center-worker-control-api
command-center-maintenance-api
command-center-updates-api
command-center-realtime
command-center-web
command-center-bootstrapper
command-center-spider-dispatcher
gatekeeper
worker-spider
worker-http-requester
worker-enum
worker-portscan
worker-highvalue
worker-techid
SERVICES
}

argus_worker_services() {
  cat <<'SERVICES'
command-center-spider-dispatcher
gatekeeper
worker-spider
worker-http-requester
worker-enum
worker-portscan
worker-highvalue
worker-techid
SERVICES
}

argus_validate_services() {
  local svc
  for svc in "$@"; do
    argus_service_project_dir "$svc" >/dev/null || argus_die "Unknown service '$svc'. Known services: $(argus_known_services | paste -sd ' ' -)"
  done
}

argus_azure_image_name() {
  local login_server="$1"
  local service="$2"
  printf '%s/%s/%s:%s\n' "$login_server" "${AZURE_IMAGE_PREFIX:-argus-engine}" "$service" "$IMAGE_TAG"
}

argus_env_args_from_file() {
  local file="$1"
  [[ -f "$file" ]] || return 0
  local keys key
  mapfile -t keys < <(argus_parse_env_keys "$file")
  # shellcheck disable=SC1090
  set -a
  source "$file"
  set +a
  for key in "${keys[@]}"; do
    printf '%s=%s\0' "$key" "${!key-}"
  done
}
