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
argus_cloud_require_env_vars "$env_file" GCP_PROJECT_ID GCP_REGION GCP_ARTIFACT_REPOSITORY

argus_cloud_gcp_ensure_login_and_project

echo "Enabling Google Cloud APIs..."
gcloud services enable \
  run.googleapis.com \
  artifactregistry.googleapis.com \
  iam.googleapis.com \
  --project "$GCP_PROJECT_ID" \
  >/dev/null

echo "Ensuring Artifact Registry Docker repository: ${GCP_ARTIFACT_REPOSITORY}"
if ! gcloud artifacts repositories describe "$GCP_ARTIFACT_REPOSITORY" \
  --project "$GCP_PROJECT_ID" \
  --location "$GCP_REGION" \
  >/dev/null 2>&1; then
  gcloud artifacts repositories create "$GCP_ARTIFACT_REPOSITORY" \
    --project "$GCP_PROJECT_ID" \
    --location "$GCP_REGION" \
    --repository-format docker \
    --description "Argus Engine worker images" \
    >/dev/null
fi

echo "Configuring Docker authentication for ${GCP_REGION}-docker.pkg.dev"
gcloud auth configure-docker "${GCP_REGION}-docker.pkg.dev" --quiet >/dev/null

echo "Google Artifact Registry is ready."
