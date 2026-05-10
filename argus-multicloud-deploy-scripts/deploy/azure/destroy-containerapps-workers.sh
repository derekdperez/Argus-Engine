#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root"

# shellcheck source=../cloud-common.sh
source "$repo_root/deploy/cloud-common.sh"
argus_cloud_load_env_file "$script_dir/.env"

: "${AZURE_RESOURCE_GROUP:?Set AZURE_RESOURCE_GROUP in deploy/azure/.env}"

if [[ "${CONFIRM_DESTROY_AZURE_ARGUS_WORKERS:-}" != "yes" ]]; then
  echo "Refusing to delete Azure Container Apps workers without confirmation." >&2
  echo "Run: CONFIRM_DESTROY_AZURE_ARGUS_WORKERS=yes $0 [service...]" >&2
  exit 2
fi

argus_cloud_require_command az

if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
  az account set --subscription "$AZURE_SUBSCRIPTION_ID"
fi

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
