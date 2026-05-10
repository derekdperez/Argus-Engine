#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

ENV_FILE="${EC2_WORKER_ENV_FILE:-$DEPLOY_DIR/ec2-worker.env}"
COMPOSE_FILE="$DEPLOY_DIR/docker-compose.ec2-workers.yml"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Missing EC2 worker env file: $ENV_FILE" >&2
  exit 2
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose v2 is required on EC2 worker machines." >&2
  exit 2
fi

# Default each worker type to exactly one local container.
spider="${EC2_WORKER_SPIDER:-1}"
enum="${EC2_WORKER_ENUM:-1}"
portscan="${EC2_WORKER_PORTSCAN:-1}"
highvalue="${EC2_WORKER_HIGHVALUE:-1}"
techid="${EC2_WORKER_TECHID:-1}"

for value in "$spider" "$enum" "$portscan" "$highvalue" "$techid"; do
  if [[ ! "$value" =~ ^[0-9]+$ ]]; then
    echo "Worker counts must be non-negative integers." >&2
    exit 2
  fi
done

services=(
  worker-spider
  worker-enum
  worker-portscan
  worker-highvalue
  worker-techid
)

build_services=()
(( spider > 0 )) && build_services+=(worker-spider)
(( enum > 0 )) && build_services+=(worker-enum)
(( portscan > 0 )) && build_services+=(worker-portscan)
(( highvalue > 0 )) && build_services+=(worker-highvalue)
(( techid > 0 )) && build_services+=(worker-techid)

if [[ ${#build_services[@]} -gt 0 ]]; then
  COMPOSE_BAKE=false docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" build "${build_services[@]}"
fi

COMPOSE_BAKE=false docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --remove-orphans \
  --scale "worker-spider=$spider" \
  --scale "worker-enum=$enum" \
  --scale "worker-portscan=$portscan" \
  --scale "worker-highvalue=$highvalue" \
  --scale "worker-techid=$techid" \
  "${services[@]}"

echo "Applied EC2 worker scale: spider=$spider enum=$enum portscan=$portscan highvalue=$highvalue techid=$techid"
