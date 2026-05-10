#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root"

# shellcheck source=../cloud-common.sh
source "$repo_root/deploy/cloud-common.sh"
argus_cloud_load_env_file "$script_dir/.env"

: "${AZURE_LOCATION:?Set AZURE_LOCATION in deploy/azure/.env}"
: "${AZURE_RESOURCE_GROUP:?Set AZURE_RESOURCE_GROUP in deploy/azure/.env}"
: "${AZURE_CONTAINERAPPS_ENV:?Set AZURE_CONTAINERAPPS_ENV in deploy/azure/.env}"
: "${AZURE_ACR_NAME:?Set AZURE_ACR_NAME in deploy/azure/.env}"

argus_cloud_require_command az

if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
  az account set --subscription "$AZURE_SUBSCRIPTION_ID"
fi

az account show >/dev/null

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
