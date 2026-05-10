#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../cloud-common.sh
source "$SCRIPT_DIR/../cloud-common.sh"

argus_warn_if_sudo
argus_require_cmd docker
argus_require_cmd az

argus_azure_bootstrap_env
argus_azure_ensure_resources

cd "$ARGUS_REPO_ROOT"

LOGIN_SERVER="$(argus_azure_acr_login_server)"

if [[ -n "${AZURE_IMAGE_PREFIX:-}" ]]; then
  :
else
  AZURE_IMAGE_PREFIX="argus-engine"
fi

argus_log "Logging Docker into Azure Container Registry: $AZURE_ACR_NAME"
az acr login --name "$AZURE_ACR_NAME" >/dev/null

# Existing Argus Dockerfiles depend on these local base tags.
if [[ -x "$ARGUS_REPO_ROOT/deploy/build-base-images.sh" ]]; then
  argus_log "Building local Argus base images."
  "$ARGUS_REPO_ROOT/deploy/build-base-images.sh"
else
  argus_log "Building local Argus base images directly."
  docker build -t argus-engine-base:local -f deploy/Dockerfile.base-runtime deploy/
  docker build -t argus-recon-base:local -f deploy/Dockerfile.base-recon deploy/
fi

SERVICES=("$@")
if [[ ${#SERVICES[@]} -eq 0 ]]; then
  mapfile -t SERVICES < <(argus_known_services)
fi
argus_validate_services "${SERVICES[@]}"

export ARGUS_ENGINE_VERSION

for service in "${SERVICES[@]}"; do
  image_local="argus-engine/${service}:${ARGUS_ENGINE_VERSION}"
  image_remote="$(argus_azure_image_name "$LOGIN_SERVER" "$service")"

  argus_log "Building $service with docker compose."
  docker compose -f deploy/docker-compose.yml build "$service"

  argus_log "Tagging $image_local -> $image_remote"
  docker tag "$image_local" "$image_remote"

  argus_log "Pushing $image_remote"
  docker push "$image_remote"
done

cat <<EOF

Build/push complete.

Images pushed to:
  $LOGIN_SERVER/$AZURE_IMAGE_PREFIX/<service>:$IMAGE_TAG

Deploy workers:
  ./deploy/azure/deploy-containerapps-workers.sh

Deploy a subset:
  ./deploy/azure/build-push-acr.sh worker-enum worker-http-requester
  ./deploy/azure/deploy-containerapps-workers.sh worker-enum worker-http-requester
EOF
