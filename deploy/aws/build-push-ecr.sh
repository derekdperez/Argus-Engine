#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

# shellcheck source=../lib-argus-service-catalog.sh
source "${repo_root}/deploy/lib-argus-service-catalog.sh"

: "${AWS_REGION:?Set AWS_REGION}"
: "${AWS_ACCOUNT_ID:?Set AWS_ACCOUNT_ID}"
: "${ECR_PREFIX:=argus-v2}"
: "${IMAGE_TAG:=latest}"

registry="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
build_stamp="${BUILD_SOURCE_STAMP:-$IMAGE_TAG}"
component_version="${COMPONENT_VERSION:-$IMAGE_TAG}"

aws ecr get-login-password --region "$AWS_REGION" |
  docker login --username AWS --password-stdin "$registry"

docker_build_and_push() {
  local service="$1"
  local dockerfile project_dir app_dll image
  dockerfile="$(argus_service_dockerfile "$service")"
  project_dir="$(argus_service_project_dir "$service")"
  app_dll="$(argus_service_app_dll "$service")"
  image="${registry}/${ECR_PREFIX}/${service}:${IMAGE_TAG}"

  local build_args=(
    --file "$dockerfile"
    --build-arg "PROJECT_DIR=${project_dir}"
    --build-arg "APP_DLL=${app_dll}"
    --build-arg "BUILD_SOURCE_STAMP=${build_stamp}"
    --build-arg "COMPONENT_VERSION=${component_version}"
  )

  if [[ "$dockerfile" == "deploy/Dockerfile.worker-enum" ]]; then
    build_args+=(
      --build-arg "SUBFINDER_VERSION=${SUBFINDER_VERSION:-2.14.0}"
      --build-arg "AMASS_VERSION=${AMASS_VERSION:-5.1.1}"
    )
  fi

  echo "Building and pushing ${image}"

  # GitHub-hosted runners are ephemeral. Prefer buildx with the GHA cache when
  # available so repeated production releases do not rebuild unchanged layers.
  if [[ "${GITHUB_ACTIONS:-}" == "true" && "${ARGUS_USE_GHA_BUILD_CACHE:-1}" == "1" ]] &&
     docker buildx version >/dev/null 2>&1; then
    docker buildx build \
      "${build_args[@]}" \
      --cache-from type=gha,scope="argus-${service}" \
      --cache-to type=gha,scope="argus-${service}",mode=max \
      --tag "$image" \
      --push \
      .
  else
    DOCKER_BUILDKIT=1 docker build "${build_args[@]}" --tag "$image" .
    docker push "$image"
  fi
}

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
  docker_build_and_push "$service"
done
