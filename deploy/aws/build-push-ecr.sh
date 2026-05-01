#!/usr/bin/env bash
set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${AWS_ACCOUNT_ID:?Set AWS_ACCOUNT_ID}"
: "${ECR_PREFIX:=nightmare-v2}"
: "${IMAGE_TAG:=latest}"

registry="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"

aws ecr get-login-password --region "$AWS_REGION"   | docker login --username AWS --password-stdin "$registry"

build_and_push() {
  local service="$1"
  local dockerfile="$2"
  local project_dir="$3"
  local app_dll="$4"
  local image="${registry}/${ECR_PREFIX}/${service}:${IMAGE_TAG}"

  local build_args=(
    -f "$dockerfile"
    --build-arg PROJECT_DIR="$project_dir"
    --build-arg APP_DLL="$app_dll"
  )

  if [[ "$dockerfile" == "deploy/Dockerfile.worker-enum" ]]; then
    build_args+=(
      --build-arg SUBFINDER_PACKAGE="${SUBFINDER_PACKAGE:-github.com/projectdiscovery/subfinder/v2/cmd/subfinder@v2.14.0}"
      --build-arg AMASS_PACKAGE="${AMASS_PACKAGE:-github.com/owasp-amass/amass/v5/cmd/amass@v5.1.1}"
    )
  fi

  echo "Building ${image}"
  docker build "${build_args[@]}" -t "$image" .

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
    command-center) build_and_push "command-center" "deploy/Dockerfile.web" "NightmareV2.CommandCenter" "NightmareV2.CommandCenter.dll" ;;
    gatekeeper) build_and_push "gatekeeper" "deploy/Dockerfile.worker" "NightmareV2.Gatekeeper" "NightmareV2.Gatekeeper.dll" ;;
    worker-spider) build_and_push "worker-spider" "deploy/Dockerfile.worker" "NightmareV2.Workers.Spider" "NightmareV2.Workers.Spider.dll" ;;
    worker-enum) build_and_push "worker-enum" "deploy/Dockerfile.worker-enum" "NightmareV2.Workers.Enum" "NightmareV2.Workers.Enum.dll" ;;
    worker-portscan) build_and_push "worker-portscan" "deploy/Dockerfile.worker" "NightmareV2.Workers.PortScan" "NightmareV2.Workers.PortScan.dll" ;;
    worker-highvalue) build_and_push "worker-highvalue" "deploy/Dockerfile.worker" "NightmareV2.Workers.HighValue" "NightmareV2.Workers.HighValue.dll" ;;
    worker-techid) build_and_push "worker-techid" "deploy/Dockerfile.worker" "NightmareV2.Workers.TechnologyIdentification" "NightmareV2.Workers.TechnologyIdentification.dll" ;;
    *) echo "Unknown service: $service" >&2; exit 1 ;;
  esac
done
