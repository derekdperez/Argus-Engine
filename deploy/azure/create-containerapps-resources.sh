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
argus_cloud_require_env_vars "$env_file" AZURE_LOCATION AZURE_RESOURCE_GROUP AZURE_CONTAINERAPPS_ENV AZURE_ACR_NAME AZURE_ACR_SKU

argus_cloud_azure_ensure_login_and_subscription

echo "Registering Azure providers..."
az provider register --namespace Microsoft.App >/dev/null
az provider register --namespace Microsoft.OperationalInsights >/dev/null

echo "Ensuring resource group: ${AZURE_RESOURCE_GROUP}"
az group create \
  --name "$AZURE_RESOURCE_GROUP" \
  --location "$AZURE_LOCATION" \
  >/dev/null

echo "Ensuring Azure Container Registry: ${AZURE_ACR_NAME}"
if ! az acr show --name "$AZURE_ACR_NAME" --resource-group "$AZURE_RESOURCE_GROUP" >/dev/null 2>&1; then
  az acr create \
    --name "$AZURE_ACR_NAME" \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --location "$AZURE_LOCATION" \
    --sku "${AZURE_ACR_SKU:-Basic}" \
    --admin-enabled true \
    >/dev/null
else
  az acr update \
    --name "$AZURE_ACR_NAME" \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --admin-enabled true \
    >/dev/null
fi

echo "Ensuring Azure Container Apps environment: ${AZURE_CONTAINERAPPS_ENV}"
if ! az containerapp env show --name "$AZURE_CONTAINERAPPS_ENV" --resource-group "$AZURE_RESOURCE_GROUP" >/dev/null 2>&1; then
  az containerapp env create \
    --name "$AZURE_CONTAINERAPPS_ENV" \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --location "$AZURE_LOCATION" \
    >/dev/null
fi

echo "Logging Docker into ACR..."
az acr login --name "$AZURE_ACR_NAME" >/dev/null

login_server="$(az acr show --name "$AZURE_ACR_NAME" --resource-group "$AZURE_RESOURCE_GROUP" --query loginServer -o tsv)"
echo "Azure resources ready."
echo "ACR login server: ${login_server}"
