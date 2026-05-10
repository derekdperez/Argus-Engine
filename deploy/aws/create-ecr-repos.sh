#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

# shellcheck source=../lib-argus-service-catalog.sh
source "${repo_root}/deploy/lib-argus-service-catalog.sh"

: "${AWS_REGION:?Set AWS_REGION}"
: "${ECR_PREFIX:=argus-v2}"

services=("$@")
if [[ ${#services[@]} -eq 0 ]]; then
  mapfile -t services < <(argus_ecr_default_services)
fi

for raw_service in "${services[@]}"; do
  service="$(argus_validate_service "$raw_service")"
  if [[ "$(argus_service_ecr_enabled "$service")" != "1" ]]; then
    echo "Skipping ${service}: not marked as an ECR/ECS deployable service in deploy/service-catalog.tsv"
    continue
  fi

  repo="${ECR_PREFIX}/${service}"
  aws ecr describe-repositories \
    --region "$AWS_REGION" \
    --repository-names "$repo" >/dev/null 2>&1 ||
    aws ecr create-repository \
      --region "$AWS_REGION" \
      --repository-name "$repo" >/dev/null

  echo "Ensured ECR repository: $repo"
done
