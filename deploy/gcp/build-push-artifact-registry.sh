#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../cloud-common.sh
source "$script_dir/../cloud-common.sh"

repo_root="$(argus_cloud_find_repo_root "$script_dir")"
cd "$repo_root"

gcp_dir="$repo_root/deploy/gcp"
mkdir -p "$gcp_dir"
env_example="$script_dir/.env.example"
[[ -f "$env_example" ]] || env_example="$gcp_dir/.env.example"
env_file="$gcp_dir/.env"

argus_cloud_ensure_config_file "$env_file" "$env_example" "Google Cloud deployment settings"
argus_cloud_load_env_file "$env_file"
argus_cloud_prompt_gcp_env "$env_file"
argus_cloud_require_env_vars "$env_file" GCP_PROJECT_ID GCP_REGION GCP_ARTIFACT_REPOSITORY GCP_IMAGE_PREFIX IMAGE_TAG

argus_cloud_require_command docker
argus_cloud_gcp_ensure_login_and_project
argus_cloud_export_build_stamp

if ! gcloud artifacts repositories describe "$GCP_ARTIFACT_REPOSITORY" \
  --project "$GCP_PROJECT_ID" \
  --location "$GCP_REGION" \
  >/dev/null 2>&1; then
  cat >&2 <<EOF
Artifact Registry repository ${GCP_ARTIFACT_REPOSITORY} was not found in ${GCP_REGION}.

Run this first:

  deploy/gcp/create-artifact-registry.sh

EOF
  exit 2
fi

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
