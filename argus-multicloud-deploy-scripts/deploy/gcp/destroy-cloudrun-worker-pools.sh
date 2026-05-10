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

if [[ "${CONFIRM_DESTROY_GCP_ARGUS_WORKERS:-}" != "yes" ]]; then
  echo "Refusing to delete Cloud Run Worker Pools without confirmation." >&2
  echo "Run: CONFIRM_DESTROY_GCP_ARGUS_WORKERS=yes $0 [service...]" >&2
  exit 2
fi

argus_cloud_require_command gcloud

gcloud config set project "$GCP_PROJECT_ID" >/dev/null

mapfile -t selected_services < <(argus_cloud_selected_services "$@")
for service in "${selected_services[@]}"; do
  suffix="$(argus_cloud_sanitize_env_suffix "$service")"
  pool_var="GCP_WORKER_POOL_NAME_${suffix}"
  pool_name="${!pool_var:-argus-${service}}"

  if gcloud run worker-pools describe "$pool_name" --project "$GCP_PROJECT_ID" --region "$GCP_REGION" >/dev/null 2>&1; then
    echo "Deleting Cloud Run Worker Pool: ${pool_name}"
    gcloud run worker-pools delete "$pool_name" \
      --project "$GCP_PROJECT_ID" \
      --region "$GCP_REGION" \
      --quiet
  else
    echo "Skipping missing Cloud Run Worker Pool: ${pool_name}"
  fi
done
