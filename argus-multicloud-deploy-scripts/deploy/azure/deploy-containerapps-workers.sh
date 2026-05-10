#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root"

# shellcheck source=../cloud-common.sh
source "$repo_root/deploy/cloud-common.sh"
argus_cloud_load_env_file "$script_dir/.env"

: "${AZURE_RESOURCE_GROUP:?Set AZURE_RESOURCE_GROUP in deploy/azure/.env}"
: "${AZURE_CONTAINERAPPS_ENV:?Set AZURE_CONTAINERAPPS_ENV in deploy/azure/.env}"
: "${AZURE_ACR_NAME:?Set AZURE_ACR_NAME in deploy/azure/.env}"
: "${AZURE_IMAGE_PREFIX:=argus-engine}"
: "${IMAGE_TAG:=latest}"
: "${SERVICE_ENV_FILE:=$script_dir/service-env}"

argus_cloud_require_command az

if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
  az account set --subscription "$AZURE_SUBSCRIPTION_ID"
fi

az account show >/dev/null

login_server="$(az acr show --name "$AZURE_ACR_NAME" --resource-group "$AZURE_RESOURCE_GROUP" --query loginServer -o tsv)"
registry_user="$(az acr credential show --name "$AZURE_ACR_NAME" --query username -o tsv)"
registry_pass="$(az acr credential show --name "$AZURE_ACR_NAME" --query 'passwords[0].value' -o tsv)"

mapfile -t selected_services < <(argus_cloud_selected_services "$@")

for service in "${selected_services[@]}"; do
  suffix="$(argus_cloud_sanitize_env_suffix "$service")"
  app_var="AZURE_APP_NAME_${suffix}"
  app_name="${!app_var:-argus-${service}}"
  image="${login_server}/${AZURE_IMAGE_PREFIX}/${service}:${IMAGE_TAG}"

  min_default="$(argus_cloud_service_default_instances "$service")"
  min_replicas="$(argus_cloud_service_var_or_default AZURE_MIN_REPLICAS "$service" "${AZURE_MIN_REPLICAS:-$min_default}")"
  max_replicas="$(argus_cloud_service_var_or_default AZURE_MAX_REPLICAS "$service" "${AZURE_MAX_REPLICAS:-3}")"
  cpu="$(argus_cloud_service_var_or_default AZURE_CPU "$service" "$(argus_cloud_service_default_cpu_azure "$service")")"
  memory="$(argus_cloud_service_var_or_default AZURE_MEMORY "$service" "$(argus_cloud_service_default_memory_azure "$service")")"

  tmp_env="$(mktemp)"
  trap 'rm -f "$tmp_env"' EXIT
  argus_cloud_write_service_env_file "$SERVICE_ENV_FILE" "$service" "$tmp_env"
  argus_cloud_env_file_to_azure_args "$tmp_env" env_args

  echo "Deploying Azure Container App worker ${app_name} from ${image}"
  if az containerapp show --name "$app_name" --resource-group "$AZURE_RESOURCE_GROUP" >/dev/null 2>&1; then
    az containerapp registry set \
      --name "$app_name" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --server "$login_server" \
      --username "$registry_user" \
      --password "$registry_pass" \
      >/dev/null

    az containerapp update \
      --name "$app_name" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --image "$image" \
      --set-env-vars "${env_args[@]}" \
      --cpu "$cpu" \
      --memory "$memory" \
      --min-replicas "$min_replicas" \
      --max-replicas "$max_replicas" \
      >/dev/null
  else
    az containerapp create \
      --name "$app_name" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --environment "$AZURE_CONTAINERAPPS_ENV" \
      --image "$image" \
      --registry-server "$login_server" \
      --registry-username "$registry_user" \
      --registry-password "$registry_pass" \
      --env-vars "${env_args[@]}" \
      --cpu "$cpu" \
      --memory "$memory" \
      --min-replicas "$min_replicas" \
      --max-replicas "$max_replicas" \
      >/dev/null
  fi

  rm -f "$tmp_env"
  trap - EXIT
  echo "Deployed ${app_name} replicas ${min_replicas}-${max_replicas}, cpu=${cpu}, memory=${memory}"
done

echo "Azure Container Apps worker deployment complete."
