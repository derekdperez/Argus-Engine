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
: "${SERVICE_ENV_FILE:=$script_dir/service-env}"

argus_cloud_require_command gcloud

gcloud config set project "$GCP_PROJECT_ID" >/dev/null

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
