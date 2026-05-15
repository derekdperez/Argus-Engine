#!/usr/bin/env bash
set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${ECS_CLUSTER:?Set ECS_CLUSTER}"

scope="${1:-workers}"

case "$scope" in
  workers)
    : "${CONFIRM_DESTROY_ECS_WORKERS:?Set CONFIRM_DESTROY_ECS_WORKERS=yes to delete worker ECS services}"
    [[ "$CONFIRM_DESTROY_ECS_WORKERS" == "yes" ]] || { echo "Refusing to delete workers without CONFIRM_DESTROY_ECS_WORKERS=yes" >&2; exit 2; }
    services=(
      "${WORKER_SPIDER_SERVICE:-argus-worker-spider}"
      "${WORKER_ENUM_SERVICE:-argus-worker-enum}"
      "${WORKER_PORTSCAN_SERVICE:-argus-worker-portscan}"
      "${WORKER_HIGHVALUE_SERVICE:-argus-worker-highvalue}"
      "${WORKER_TECHID_SERVICE:-argus-worker-techid}"
    )
    ;;
  all)
    : "${CONFIRM_DESTROY_ECS_ALL:?Set CONFIRM_DESTROY_ECS_ALL=yes to delete all argus ECS services}"
    [[ "$CONFIRM_DESTROY_ECS_ALL" == "yes" ]] || { echo "Refusing to delete all services without CONFIRM_DESTROY_ECS_ALL=yes" >&2; exit 2; }
    services=(
      "${COMMAND_CENTER_SERVICE:-argus-command-center}"
      "${GATEKEEPER_SERVICE:-argus-gatekeeper}"
      "${WORKER_SPIDER_SERVICE:-argus-worker-spider}"
      "${WORKER_ENUM_SERVICE:-argus-worker-enum}"
      "${WORKER_PORTSCAN_SERVICE:-argus-worker-portscan}"
      "${WORKER_HIGHVALUE_SERVICE:-argus-worker-highvalue}"
      "${WORKER_TECHID_SERVICE:-argus-worker-techid}"
    )
    ;;
  *)
    echo "Usage: $0 [workers|all]" >&2
    exit 2
    ;;
esac

for service in "${services[@]}"; do
  status="$(
    aws ecs describe-services \
      --region "$AWS_REGION" \
      --cluster "$ECS_CLUSTER" \
      --services "$service" \
      --query 'services[0].status' \
      --output text 2>/dev/null || true
  )"

  if [[ "$status" != "ACTIVE" && "$status" != "DRAINING" ]]; then
    echo "Skipping ${service}; status=${status:-missing}"
    continue
  fi

  aws ecs update-service \
    --region "$AWS_REGION" \
    --cluster "$ECS_CLUSTER" \
    --service "$service" \
    --desired-count 0 >/dev/null

  aws ecs delete-service \
    --region "$AWS_REGION" \
    --cluster "$ECS_CLUSTER" \
    --service "$service" \
    --force >/dev/null

  echo "Deleted ECS service ${service}"
done
