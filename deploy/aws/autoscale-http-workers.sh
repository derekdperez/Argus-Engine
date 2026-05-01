#!/usr/bin/env bash
set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${ECS_CLUSTER:?Set ECS_CLUSTER}"
: "${COMMAND_CENTER_URL:?Set COMMAND_CENTER_URL}"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec "${script_dir}/autoscale-ecs-workers.sh" worker-spider
