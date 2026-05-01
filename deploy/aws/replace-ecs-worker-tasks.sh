#!/usr/bin/env bash
set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${ECS_CLUSTER:?Set ECS_CLUSTER}"
: "${ECS_REPLACE_WORKERS_WAIT:=true}"

workers=("$@")
if [[ ${#workers[@]} -eq 0 ]]; then
  workers=(worker-spider worker-enum worker-portscan worker-highvalue worker-techid)
fi

service_name() {
  case "$1" in
    worker-spider) echo "${WORKER_SPIDER_SERVICE:-nightmare-worker-spider}" ;;
    worker-enum) echo "${WORKER_ENUM_SERVICE:-nightmare-worker-enum}" ;;
    worker-portscan) echo "${WORKER_PORTSCAN_SERVICE:-nightmare-worker-portscan}" ;;
    worker-highvalue) echo "${WORKER_HIGHVALUE_SERVICE:-nightmare-worker-highvalue}" ;;
    worker-techid) echo "${WORKER_TECHID_SERVICE:-nightmare-worker-techid}" ;;
    *) echo "Unknown worker key: $1" >&2; return 1 ;;
  esac
}

active_services=()

for worker in "${workers[@]}"; do
  service="$(service_name "$worker")"
  status="$(
    aws ecs describe-services \
      --region "$AWS_REGION" \
      --cluster "$ECS_CLUSTER" \
      --services "$service" \
      --query 'services[0].status' \
      --output text 2>/dev/null || true
  )"

  if [[ "$status" != "ACTIVE" ]]; then
    echo "Skipping ${service}; status=${status:-missing}"
    continue
  fi

  active_services+=("$service")
  aws ecs update-service \
    --region "$AWS_REGION" \
    --cluster "$ECS_CLUSTER" \
    --service "$service" \
    --desired-count 0 >/dev/null
  echo "Scaled ${service} to zero to replace worker tasks"
done

if [[ "$ECS_REPLACE_WORKERS_WAIT" == "true" && ${#active_services[@]} -gt 0 ]]; then
  echo "Waiting for worker services to stop existing tasks..."
  aws ecs wait services-stable \
    --region "$AWS_REGION" \
    --cluster "$ECS_CLUSTER" \
    --services "${active_services[@]}"
fi
