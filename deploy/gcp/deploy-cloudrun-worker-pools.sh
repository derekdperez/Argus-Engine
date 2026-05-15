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
argus_cloud_require_env_vars "$env_file" GCP_PROJECT_ID GCP_REGION GCP_ARTIFACT_REPOSITORY GCP_IMAGE_PREFIX IMAGE_TAG SERVICE_ENV_FILE

SERVICE_ENV_FILE="$(argus_cloud_abs_path_from_repo "$repo_root" "$SERVICE_ENV_FILE")"
service_env_example="$script_dir/service-env.example"
[[ -f "$service_env_example" ]] || service_env_example="$gcp_dir/service-env.example"
argus_cloud_ensure_service_env "$SERVICE_ENV_FILE" "$service_env_example" "Cloud Run Worker Pools"

argus_cloud_gcp_ensure_login_and_project

registry="${GCP_REGION}-docker.pkg.dev/${GCP_PROJECT_ID}/${GCP_ARTIFACT_REPOSITORY}"
mapfile -t selected_services < <(argus_cloud_selected_services "$@")

for service in "${selected_services[@]}"; do
  suffix="$(argus_cloud_sanitize_env_suffix "$service")"
  pool_var="GCP_WORKER_POOL_NAME_${suffix}"
  pool_name="${!pool_var:-argus-${service}}"
  image="${registry}/${GCP_IMAGE_PREFIX}/${service}:${IMAGE_TAG}"

  default_instances="$(argus_cloud_service_default_instances "$service")"
  instances="$(argus_cloud_service_var_or_default GCP_WORKER_INSTANCES "$service" "${GCP_WORKER_INSTANCES:-$default_instances}")"
  cpu="$(argus_cloud_service_var_or_default GCP_CPU "$service" "$(argus_cloud_service_default_cpu_gcp "$service")")"
  memory="$(argus_cloud_service_var_or_default GCP_MEMORY "$service" "$(argus_cloud_service_default_memory_gcp "$service")")"

  tmp_env="$(mktemp)"
  trap 'rm -f "$tmp_env"' EXIT
  argus_cloud_write_service_env_file "$SERVICE_ENV_FILE" "$service" "$tmp_env"

  args=(
    run worker-pools deploy "$pool_name"
    --project "$GCP_PROJECT_ID"
    --region "$GCP_REGION"
    --image "$image"
    --instances "$instances"
    --cpu "$cpu"
    --memory "$memory"
    --env-vars-file "$tmp_env"
    --quiet
  )

  if [[ -n "${GCP_SERVICE_ACCOUNT:-}" ]]; then
    args+=(--service-account "$GCP_SERVICE_ACCOUNT")
  fi
  if [[ -n "${GCP_VPC_CONNECTOR:-}" ]]; then
    args+=(--vpc-connector "$GCP_VPC_CONNECTOR")
  fi
  if [[ -n "${GCP_VPC_EGRESS:-}" ]]; then
    args+=(--vpc-egress "$GCP_VPC_EGRESS")
  fi

  echo "Deploying Cloud Run Worker Pool ${pool_name} from ${image}"
  gcloud "${args[@]}"

  rm -f "$tmp_env"
  trap - EXIT
  echo "Deployed ${pool_name} instances=${instances}, cpu=${cpu}, memory=${memory}"
done

echo "Cloud Run Worker Pool deployment complete."
