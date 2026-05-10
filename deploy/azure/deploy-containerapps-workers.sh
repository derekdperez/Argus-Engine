#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../cloud-common.sh
source "$script_dir/../cloud-common.sh"

repo_root="$(argus_cloud_find_repo_root "$script_dir")"
cd "$repo_root"

azure_dir="$repo_root/deploy/azure"
mkdir -p "$azure_dir"
env_example="$script_dir/.env.example"
[[ -f "$env_example" ]] || env_example="$azure_dir/.env.example"
env_file="$azure_dir/.env"

argus_cloud_ensure_config_file "$env_file" "$env_example" "Azure deployment settings"
argus_cloud_load_env_file "$env_file"
argus_cloud_prompt_azure_env "$env_file"
argus_cloud_require_env_vars "$env_file" AZURE_RESOURCE_GROUP AZURE_CONTAINERAPPS_ENV AZURE_ACR_NAME AZURE_IMAGE_PREFIX IMAGE_TAG SERVICE_ENV_FILE

SERVICE_ENV_FILE="$(argus_cloud_abs_path_from_repo "$repo_root" "$SERVICE_ENV_FILE")"
service_env_example="$script_dir/service-env.example"
[[ -f "$service_env_example" ]] || service_env_example="$azure_dir/service-env.example"
argus_cloud_ensure_service_env "$SERVICE_ENV_FILE" "$service_env_example" "Azure Container Apps"

argus_cloud_azure_ensure_login_and_subscription

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
