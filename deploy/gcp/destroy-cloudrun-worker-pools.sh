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
argus_cloud_require_env_vars "$env_file" GCP_PROJECT_ID GCP_REGION

if [[ "${CONFIRM_DESTROY_GCP_ARGUS_WORKERS:-}" != "yes" ]]; then
  echo "Refusing to delete Cloud Run Worker Pools without confirmation." >&2
  echo "Run: CONFIRM_DESTROY_GCP_ARGUS_WORKERS=yes $0 [service...]" >&2
  exit 2
fi

argus_cloud_gcp_ensure_login_and_project

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
