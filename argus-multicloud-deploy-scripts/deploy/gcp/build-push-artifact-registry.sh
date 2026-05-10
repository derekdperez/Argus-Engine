#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root"

# shellcheck source=../cloud-common.sh
source "$repo_root/deploy/cloud-common.sh"
argus_cloud_load_env_file "$script_dir/.env"

: "${GCP_PROJECT_ID:?Set GCP_PROJECT_ID in deploy/gcp/.env}"
: "${GCP_REGION:?Set GCP_REGION in deploy/gcp/.env}"
: "${GCP_ARTIFACT_REPOSITORY:?Set GCP_ARTIFACT_REPOSITORY in deploy/gcp/.env}"
: "${GCP_IMAGE_PREFIX:=argus-engine}"
: "${IMAGE_TAG:=latest}"

argus_cloud_require_command gcloud
argus_cloud_require_command docker
argus_cloud_export_build_stamp

gcloud config set project "$GCP_PROJECT_ID" >/dev/null
gcloud auth configure-docker "${GCP_REGION}-docker.pkg.dev" --quiet >/dev/null

registry="${GCP_REGION}-docker.pkg.dev/${GCP_PROJECT_ID}/${GCP_ARTIFACT_REPOSITORY}"

argus_cloud_build_base_images

mapfile -t selected_services < <(argus_cloud_selected_services "$@")
for service in "${selected_services[@]}"; do
  image="${registry}/${GCP_IMAGE_PREFIX}/${service}:${IMAGE_TAG}"
  argus_cloud_build_service_image "$service" "$image"
  docker push "$image"
done

echo "Pushed ${#selected_services[@]} image(s) to ${registry}/${GCP_IMAGE_PREFIX}: tag ${IMAGE_TAG}"
