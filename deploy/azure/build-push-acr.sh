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
argus_cloud_require_env_vars "$env_file" AZURE_RESOURCE_GROUP AZURE_ACR_NAME AZURE_IMAGE_PREFIX IMAGE_TAG

argus_cloud_require_command docker
argus_cloud_azure_ensure_login_and_subscription
argus_cloud_export_build_stamp

if ! az acr show --name "$AZURE_ACR_NAME" --resource-group "$AZURE_RESOURCE_GROUP" >/dev/null 2>&1; then
  cat >&2 <<EOF
Azure Container Registry ${AZURE_ACR_NAME} was not found in ${AZURE_RESOURCE_GROUP}.

Run this first:

  deploy/azure/create-containerapps-resources.sh

EOF
  exit 2
fi

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
