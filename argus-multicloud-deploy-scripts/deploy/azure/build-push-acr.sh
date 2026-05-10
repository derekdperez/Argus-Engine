#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root"

# shellcheck source=../cloud-common.sh
source "$repo_root/deploy/cloud-common.sh"
argus_cloud_load_env_file "$script_dir/.env"

: "${AZURE_RESOURCE_GROUP:?Set AZURE_RESOURCE_GROUP in deploy/azure/.env}"
: "${AZURE_ACR_NAME:?Set AZURE_ACR_NAME in deploy/azure/.env}"
: "${AZURE_IMAGE_PREFIX:=argus-engine}"
: "${IMAGE_TAG:=latest}"

argus_cloud_require_command az
argus_cloud_require_command docker
argus_cloud_export_build_stamp

if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
  az account set --subscription "$AZURE_SUBSCRIPTION_ID"
fi

az account show >/dev/null
az acr login --name "$AZURE_ACR_NAME" >/dev/null

login_server="$(az acr show --name "$AZURE_ACR_NAME" --resource-group "$AZURE_RESOURCE_GROUP" --query loginServer -o tsv)"

argus_cloud_build_base_images

mapfile -t selected_services < <(argus_cloud_selected_services "$@")
for service in "${selected_services[@]}"; do
  image="${login_server}/${AZURE_IMAGE_PREFIX}/${service}:${IMAGE_TAG}"
  argus_cloud_build_service_image "$service" "$image"
  docker push "$image"
done

echo "Pushed ${#selected_services[@]} image(s) to ${login_server}/${AZURE_IMAGE_PREFIX}: tag ${IMAGE_TAG}"
