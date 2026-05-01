#!/usr/bin/env bash
set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${ECS_CLUSTER:?Set ECS_CLUSTER}"
: "${COMMAND_CENTER_URL:?Set COMMAND_CENTER_URL}"
: "${ECS_AUTOSCALER_UPDATE_TASK_DEFINITION:=true}"
: "${ECS_AUTOSCALER_FORCE_NEW_DEPLOYMENT:=false}"

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

worker_keys() {
  case "$1" in
    worker-spider) echo "Spider" ;;
    worker-enum) echo "Enum" ;;
    worker-portscan) echo "PortScan" ;;
    worker-highvalue) echo "HighValueRegex,HighValuePaths" ;;
    worker-techid) echo "TechnologyIdentification" ;;
    *) return 1 ;;
  esac
}

task_family() {
  local worker="$1"
  local sanitized="${worker//-/_}"
  local var="ECS_TASK_FAMILY_${sanitized^^}"
  echo "${!var:-nightmare-v2-${worker}}"
}

scale_min() {
  local worker="$1"
  local sanitized="${worker//-/_}"
  local var="ECS_MIN_${sanitized^^}"
  case "$worker" in
    worker-spider) echo "${!var:-${HTTP_QUEUE_MIN_TASKS:-1}}" ;;
    *) echo "${!var:-1}" ;;
  esac
}

scale_max() {
  local worker="$1"
  local sanitized="${worker//-/_}"
  local var="ECS_MAX_${sanitized^^}"
  case "$worker" in
    worker-spider) echo "${!var:-${HTTP_QUEUE_MAX_TASKS:-50}}" ;;
    *) echo "${!var:-20}" ;;
  esac
}

target_backlog() {
  local worker="$1"
  local sanitized="${worker//-/_}"
  local var="ECS_TARGET_BACKLOG_PER_TASK_${sanitized^^}"
  case "$worker" in
    worker-spider) echo "${!var:-${HTTP_QUEUE_TARGET_BACKLOG_PER_TASK:-100}}" ;;
    worker-enum) echo "${!var:-25}" ;;
    worker-portscan) echo "${!var:-100}" ;;
    worker-highvalue) echo "${!var:-100}" ;;
    worker-techid) echo "${!var:-100}" ;;
    *) echo "${!var:-100}" ;;
  esac
}

desired_for() {
  local backlog="$1"
  local target="$2"
  local min="$3"
  local max="$4"
  local desired=$(( (backlog + target - 1) / target ))
  if (( desired < min )); then desired="$min"; fi
  if (( desired > max )); then desired="$max"; fi
  echo "$desired"
}

latest_task_definition() {
  local family="$1"
  aws ecs list-task-definitions \
    --region "$AWS_REGION" \
    --family-prefix "$family" \
    --status ACTIVE \
    --sort DESC \
    --max-items 1 \
    --query 'taskDefinitionArns[0]' \
    --output text
}

http_metrics_json=""
rabbit_queues_json=""

for worker in "${workers[@]}"; do
  ecs_service="$(service_name "$worker")"
  min_tasks="$(scale_min "$worker")"
  max_tasks="$(scale_max "$worker")"
  target="$(target_backlog "$worker")"

  if [[ "$worker" == "worker-spider" ]]; then
    if [[ -z "$http_metrics_json" ]]; then
      http_metrics_json="$(curl -fsS "${COMMAND_CENTER_URL%/}/api/http-request-queue/metrics")"
    fi
    backlog="$(HTTP_METRICS_JSON="$http_metrics_json" python3 -c 'import json,os; m=json.loads(os.environ["HTTP_METRICS_JSON"]); print(int(m.get("backlogCount", 0)))')"
  else
    if [[ -z "$rabbit_queues_json" ]]; then
      rabbit_queues_json="$(curl -fsS "${COMMAND_CENTER_URL%/}/api/ops/rabbit-queues")"
    fi
    backlog="$(RABBIT_QUEUES_JSON="$rabbit_queues_json" WORKER_KEYS="$(worker_keys "$worker")" python3 -c '
import json
import os
rows = json.loads(os.environ["RABBIT_QUEUES_JSON"])
keys = {k.strip() for k in os.environ["WORKER_KEYS"].split(",") if k.strip()}
total = 0
for row in rows:
    if row.get("likelyWorkerKey") in keys:
        total += int(row.get("messagesReady", 0)) + int(row.get("messagesUnacknowledged", 0))
print(total)
')"
  fi

  desired="$(desired_for "$backlog" "$target" "$min_tasks" "$max_tasks")"
  current_desired="$(
    aws ecs describe-services \
      --region "$AWS_REGION" \
      --cluster "$ECS_CLUSTER" \
      --services "$ecs_service" \
      --query 'services[0].desiredCount' \
      --output text
  )"
  current_task="$(
    aws ecs describe-services \
      --region "$AWS_REGION" \
      --cluster "$ECS_CLUSTER" \
      --services "$ecs_service" \
      --query 'services[0].taskDefinition' \
      --output text
  )"

  update_args=(
    ecs update-service
    --region "$AWS_REGION"
    --cluster "$ECS_CLUSTER"
    --service "$ecs_service"
    --desired-count "$desired"
  )

  latest_task=""
  task_changed="false"
  if [[ "$ECS_AUTOSCALER_UPDATE_TASK_DEFINITION" == "true" ]]; then
    latest_task="$(latest_task_definition "$(task_family "$worker")")"
    if [[ "$latest_task" != "None" && -n "$latest_task" && "$latest_task" != "$current_task" ]]; then
      update_args+=(--task-definition "$latest_task")
      task_changed="true"
    fi
  fi

  if [[ "$ECS_AUTOSCALER_FORCE_NEW_DEPLOYMENT" == "true" || "$task_changed" == "true" ]]; then
    update_args+=(--force-new-deployment)
  fi

  echo "${worker}: backlog=${backlog} currentDesired=${current_desired} targetBacklogPerTask=${target} nextDesired=${desired}"

  if [[ "$current_desired" != "$desired" || "$task_changed" == "true" || "$ECS_AUTOSCALER_FORCE_NEW_DEPLOYMENT" == "true" ]]; then
    aws "${update_args[@]}" >/dev/null
    if [[ "$task_changed" == "true" ]]; then
      echo "Updated ${ecs_service} to desired=${desired} taskDefinition=${latest_task}"
    else
      echo "Updated ${ecs_service} to desired=${desired}"
    fi
  else
    echo "No scale change needed for ${ecs_service}"
  fi
done
