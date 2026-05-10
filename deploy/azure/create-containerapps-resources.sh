#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../cloud-common.sh
source "$SCRIPT_DIR/../cloud-common.sh"

argus_warn_if_sudo
argus_azure_bootstrap_env
argus_azure_ensure_resources

LOGIN_SERVER="$(argus_azure_acr_login_server)"

cat <<EOF
Azure resources are ready.

Resource group:          $AZURE_RESOURCE_GROUP
Location:                $AZURE_LOCATION
Container Apps env:      $AZURE_CONTAINERAPPS_ENV
Container Registry:      $AZURE_ACR_NAME
ACR login server:        $LOGIN_SERVER

Next:
  ./deploy/azure/build-push-acr.sh
  ./deploy/azure/deploy-containerapps-workers.sh
EOF
