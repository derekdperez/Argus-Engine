#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../cloud-common.sh
source "$SCRIPT_DIR/../cloud-common.sh"

argus_warn_if_sudo
argus_require_cmd az

argus_azure_bootstrap_env
argus_azure_bootstrap_service_env
argus_azure_ensure_resources

SERVICE_ENV_FILE="$(argus_azure_service_env_file)"
LOGIN_SERVER="$(argus_azure_acr_login_server)"
ACR_USER="$(argus_azure_acr_username)"
ACR_PASS="$(argus_azure_acr_password)"

SERVICES=("$@")
if [[ ${#SERVICES[@]} -eq 0 ]]; then
  mapfile -t SERVICES < <(argus_worker_services)
fi
argus_validate_services "${SERVICES[@]}"

mapfile -d '' ENV_PAIRS < <(argus_env_args_from_file "$SERVICE_ENV_FILE")

for service in "${SERVICES[@]}"; do
  app_name="argus-${service}"
  image_remote="$(argus_azure_image_name "$LOGIN_SERVER" "$service")"

  argus_log "Deploying $app_name from $image_remote"

  if az containerapp show --resource-group "$AZURE_RESOURCE_GROUP" --name "$app_name" >/dev/null 2>&1; then
    az containerapp update \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$app_name" \
      --image "$image_remote" \
      --set-env-vars "${ENV_PAIRS[@]}" \
      --output none

    az containerapp registry set \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$app_name" \
      --server "$LOGIN_SERVER" \
      --username "$ACR_USER" \
      --password "$ACR_PASS" \
      --output none

    az containerapp update \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$app_name" \
      --min-replicas 1 \
      --max-replicas 2 \
      --cpu 0.5 \
      --memory 1.0Gi \
      --output none
  else
    az containerapp create \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --environment "$AZURE_CONTAINERAPPS_ENV" \
      --name "$app_name" \
      --image "$image_remote" \
      --registry-server "$LOGIN_SERVER" \
      --registry-username "$ACR_USER" \
      --registry-password "$ACR_PASS" \
      --ingress disabled \
      --min-replicas 1 \
      --max-replicas 2 \
      --cpu 0.5 \
      --memory 1.0Gi \
      --env-vars "${ENV_PAIRS[@]}" \
      --output none
  fi

  argus_log "Deployed $app_name"
done

cat <<EOF

Worker deployment complete.

List apps:
  az containerapp list -g "$AZURE_RESOURCE_GROUP" -o table

Follow logs for one worker:
  az containerapp logs show -g "$AZURE_RESOURCE_GROUP" -n argus-worker-enum --follow
EOF
