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
argus_cloud_require_env_vars "$env_file" AZURE_RESOURCE_GROUP

if [[ "${CONFIRM_DESTROY_AZURE_ARGUS_WORKERS:-}" != "yes" ]]; then
  echo "Refusing to delete Azure Container Apps workers without confirmation." >&2
  echo "Run: CONFIRM_DESTROY_AZURE_ARGUS_WORKERS=yes $0 [service...]" >&2
  exit 2
fi

argus_cloud_azure_ensure_login_and_subscription

mapfile -t selected_services < <(argus_cloud_selected_services "$@")
for service in "${selected_services[@]}"; do
  suffix="$(argus_cloud_sanitize_env_suffix "$service")"
  app_var="AZURE_APP_NAME_${suffix}"
  app_name="${!app_var:-argus-${service}}"

  if az containerapp show --name "$app_name" --resource-group "$AZURE_RESOURCE_GROUP" >/dev/null 2>&1; then
    echo "Deleting Azure Container App: ${app_name}"
    az containerapp delete --name "$app_name" --resource-group "$AZURE_RESOURCE_GROUP" --yes >/dev/null
  else
    echo "Skipping missing Azure Container App: ${app_name}"
  fi
done
