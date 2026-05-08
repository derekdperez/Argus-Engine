#!/usr/bin/env bash
set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${AWS_ACCOUNT_ID:?Set AWS_ACCOUNT_ID}"
: "${ECR_PREFIX:=argus-v2}"
: "${IMAGE_TAG:=latest}"

registry="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
build_stamp="${BUILD_SOURCE_STAMP:-$IMAGE_TAG}"
component_version="${COMPONENT_VERSION:-$IMAGE_TAG}"

aws ecr get-login-password --region "$AWS_REGION" |
  docker login --username AWS --password-stdin "$registry"

build_and_push() {
  local service="$1"
  local dockerfile="$2"
  local project_dir="$3"
  local app_dll="$4"
  local image="${registry}/${ECR_PREFIX}/${service}:${IMAGE_TAG}"

  local build_args=(
    -f "$dockerfile"
    --build-arg "PROJECT_DIR=${project_dir}"
    --build-arg "APP_DLL=${app_dll}"
    --build-arg "BUILD_SOURCE_STAMP=${build_stamp}"
    --build-arg "COMPONENT_VERSION=${component_version}"
  )

  if [[ "$dockerfile" == "deploy/Dockerfile.worker-enum" ]]; then
    build_args+=(
      --build-arg "SUBFINDER_VERSION=${SUBFINDER_VERSION:-2.14.0}"
      --build-arg "AMASS_VERSION=${AMASS_VERSION:-5.1.1}"
      --build-arg "SUBFINDER_PACKAGE=${SUBFINDER_PACKAGE:-github.com/projectdiscovery/subfinder/v2/cmd/subfinder@v2.14.0}"
      --build-arg "AMASS_PACKAGE=${AMASS_PACKAGE:-github.com/owasp-amass/amass/v5/cmd/amass@v5.1.1}"
    )
  fi

  echo "Building ${image}"
  DOCKER_BUILDKIT=1 docker build "${build_args[@]}" -t "$image" .
  docker push "$image"
}

services=("$@")

if [[ ${#services[@]} -eq 0 ]]; then
  services=(
    "command-center"
    "gatekeeper"
    "worker-spider"
    "worker-enum"
    "worker-portscan"
    "worker-highvalue"
    "worker-techid"
  )
fi

for service in "${services[@]}"; do
  case "$service" in
    command-center)
      build_and_push "command-center" "deploy/Dockerfile.web" "ArgusEngine.CommandCenter" "ArgusEngine.CommandCenter.dll"
      ;;
    gatekeeper)
      build_and_push "gatekeeper" "deploy/Dockerfile.worker" "ArgusEngine.Gatekeeper" "ArgusEngine.Gatekeeper.dll"
      ;;
    worker-spider)
      build_and_push "worker-spider" "deploy/Dockerfile.worker" "ArgusEngine.Workers.Spider" "ArgusEngine.Workers.Spider.dll"
      ;;
    worker-enum)
      build_and_push "worker-enum" "deploy/Dockerfile.worker-enum" "ArgusEngine.Workers.Enumeration" "ArgusEngine.Workers.Enumeration.dll"
      ;;
    worker-portscan)
      build_and_push "worker-portscan" "deploy/Dockerfile.worker" "ArgusEngine.Workers.PortScan" "ArgusEngine.Workers.PortScan.dll"
      ;;
    worker-highvalue)
      build_and_push "worker-highvalue" "deploy/Dockerfile.worker" "ArgusEngine.Workers.HighValue" "ArgusEngine.Workers.HighValue.dll"
      ;;
    worker-techid)
      build_and_push "worker-techid" "deploy/Dockerfile.worker" "ArgusEngine.Workers.TechnologyIdentification" "ArgusEngine.Workers.TechnologyIdentification.dll"
      ;;
    *)
      echo "Unknown service: $service" >&2
      exit 1
      ;;
  esac
done
