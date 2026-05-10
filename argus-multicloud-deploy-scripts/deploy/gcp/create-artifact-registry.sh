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

argus_cloud_require_command gcloud

gcloud config set project "$GCP_PROJECT_ID" >/dev/null

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
