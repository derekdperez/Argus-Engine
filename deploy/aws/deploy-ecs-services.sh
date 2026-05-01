#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

: "${AWS_REGION:?Set AWS_REGION}"
: "${AWS_ACCOUNT_ID:?Set AWS_ACCOUNT_ID}"
: "${ECS_CLUSTER:?Set ECS_CLUSTER}"
: "${ECS_TASK_EXECUTION_ROLE_ARN:?Set ECS_TASK_EXECUTION_ROLE_ARN}"
: "${ECS_SUBNETS:?Set ECS_SUBNETS as a comma-separated subnet list}"
: "${ECS_SECURITY_GROUPS:?Set ECS_SECURITY_GROUPS as a comma-separated security group list}"
: "${ECR_PREFIX:=nightmare-v2}"
: "${IMAGE_TAG:=latest}"
: "${ECS_LAUNCH_TYPE:=FARGATE}"
: "${ECS_NETWORK_MODE:=awsvpc}"
: "${ECS_ASSIGN_PUBLIC_IP:=DISABLED}"
: "${ECS_LOG_GROUP:=/ecs/nightmare-v2}"
: "${ECS_LOG_PREFIX:=nightmare-v2}"
: "${ECS_CREATE_LOG_GROUP:=true}"
: "${SERVICE_ENV_FILE:=${script_dir}/service-env}"
: "${UPDATE_DESIRED_COUNTS:=false}"

export AWS_REGION
export ECS_CLUSTER
export ECS_LAUNCH_TYPE
export ECS_NETWORK_MODE
export ECS_ASSIGN_PUBLIC_IP
export ECS_LOG_GROUP
export ECS_LOG_PREFIX
export ECS_TASK_EXECUTION_ROLE_ARN
export ECS_TASK_ROLE_ARN="${ECS_TASK_ROLE_ARN:-}"
export ECS_SUBNETS
export ECS_SECURITY_GROUPS

if [[ ! -f "$SERVICE_ENV_FILE" ]]; then
  echo "SERVICE_ENV_FILE does not exist: ${SERVICE_ENV_FILE}" >&2
  echo "Create it from deploy/aws/service-env.example and replace local hostnames/passwords with production values." >&2
  exit 2
fi

if [[ "${ECS_CREATE_LOG_GROUP}" == "true" ]]; then
  aws logs create-log-group --region "$AWS_REGION" --log-group-name "$ECS_LOG_GROUP" >/dev/null 2>&1 || true
fi

registry="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"

all_services=(
  command-center
  gatekeeper
  worker-spider
  worker-enum
  worker-portscan
  worker-highvalue
  worker-techid
)

selected_services=("$@")
if [[ ${#selected_services[@]} -eq 0 ]]; then
  selected_services=("${all_services[@]}")
fi

service_name() {
  case "$1" in
    command-center) echo "${COMMAND_CENTER_SERVICE:-nightmare-command-center}" ;;
    gatekeeper) echo "${GATEKEEPER_SERVICE:-nightmare-gatekeeper}" ;;
    worker-spider) echo "${WORKER_SPIDER_SERVICE:-nightmare-worker-spider}" ;;
    worker-enum) echo "${WORKER_ENUM_SERVICE:-nightmare-worker-enum}" ;;
    worker-portscan) echo "${WORKER_PORTSCAN_SERVICE:-nightmare-worker-portscan}" ;;
    worker-highvalue) echo "${WORKER_HIGHVALUE_SERVICE:-nightmare-worker-highvalue}" ;;
    worker-techid) echo "${WORKER_TECHID_SERVICE:-nightmare-worker-techid}" ;;
    *) echo "Unknown service key: $1" >&2; return 1 ;;
  esac
}

task_family() {
  local service="$1"
  local sanitized="${service//-/_}"
  local var="ECS_TASK_FAMILY_${sanitized^^}"
  echo "${!var:-nightmare-v2-${service}}"
}

desired_count() {
  local service="$1"
  local sanitized="${service//-/_}"
  local var="ECS_DESIRED_${sanitized^^}"
  case "$service" in
    command-center|gatekeeper) echo "${!var:-1}" ;;
    worker-spider|worker-enum) echo "${!var:-1}" ;;
    *) echo "${!var:-1}" ;;
  esac
}

task_cpu() {
  local service="$1"
  local sanitized="${service//-/_}"
  local var="ECS_CPU_${sanitized^^}"
  case "$service" in
    command-center) echo "${!var:-1024}" ;;
    worker-enum|worker-spider) echo "${!var:-1024}" ;;
    *) echo "${!var:-512}" ;;
  esac
}

task_memory() {
  local service="$1"
  local sanitized="${service//-/_}"
  local var="ECS_MEMORY_${sanitized^^}"
  case "$service" in
    command-center) echo "${!var:-2048}" ;;
    worker-enum|worker-spider) echo "${!var:-2048}" ;;
    *) echo "${!var:-1024}" ;;
  esac
}

register_task_definition() {
  local service="$1"
  local family image tmp
  family="$(task_family "$service")"
  image="${registry}/${ECR_PREFIX}/${service}:${IMAGE_TAG}"
  tmp="$(mktemp)"

  SERVICE_KEY="$service" \
  TASK_FAMILY="$family" \
  CONTAINER_IMAGE="$image" \
  TASK_CPU="$(task_cpu "$service")" \
  TASK_MEMORY="$(task_memory "$service")" \
  python3 - "$SERVICE_ENV_FILE" >"$tmp" <<'PY'
import json
import os
import sys

env_file = sys.argv[1]
service = os.environ["SERVICE_KEY"]

def parse_env(path):
    values = {}
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip('"').strip("'")
    return values

env = parse_env(env_file)

if service == "command-center":
    env.update({
        "ASPNETCORE_URLS": "http://+:8080",
        "Nightmare__ListenPlainHttp": "true",
    })
else:
    env.update({
        "Nightmare__SkipStartupDatabase": "true",
        "NIGHTMARE_SKIP_STARTUP_DATABASE": "1",
    })

if service == "worker-spider":
    env.setdefault("Spider__Http__AllowInsecureSsl", "false")

environment = [{"name": key, "value": value} for key, value in sorted(env.items())]
container = {
    "name": service,
    "image": os.environ["CONTAINER_IMAGE"],
    "essential": True,
    "environment": environment,
    "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
            "awslogs-group": os.environ["ECS_LOG_GROUP"],
            "awslogs-region": os.environ["AWS_REGION"],
            "awslogs-stream-prefix": os.environ["ECS_LOG_PREFIX"],
        },
    },
}

if service == "command-center":
    container["portMappings"] = [{
        "containerPort": 8080,
        "hostPort": 8080,
        "protocol": "tcp",
    }]
    container["healthCheck"] = {
        "command": ["CMD-SHELL", "curl -fsS http://127.0.0.1:8080/health/ready >/dev/null || exit 1"],
        "interval": 30,
        "timeout": 5,
        "retries": 3,
        "startPeriod": 60,
    }

task = {
    "family": os.environ["TASK_FAMILY"],
    "networkMode": os.environ["ECS_NETWORK_MODE"],
    "requiresCompatibilities": [os.environ["ECS_LAUNCH_TYPE"]],
    "cpu": os.environ["TASK_CPU"],
    "memory": os.environ["TASK_MEMORY"],
    "executionRoleArn": os.environ["ECS_TASK_EXECUTION_ROLE_ARN"],
    "containerDefinitions": [container],
}

task_role = os.environ.get("ECS_TASK_ROLE_ARN", "").strip()
if task_role:
    task["taskRoleArn"] = task_role

print(json.dumps(task, separators=(",", ":")))
PY

  aws ecs register-task-definition \
    --region "$AWS_REGION" \
    --cli-input-json "file://${tmp}" \
    --query 'taskDefinition.taskDefinitionArn' \
    --output text
  rm -f "$tmp"
}

service_status() {
  local ecs_service="$1"
  aws ecs describe-services \
    --region "$AWS_REGION" \
    --cluster "$ECS_CLUSTER" \
    --services "$ecs_service" \
    --query 'services[0].status' \
    --output text 2>/dev/null || true
}

create_service() {
  local service="$1"
  local ecs_service="$2"
  local task_definition="$3"
  local desired="$4"
  local tmp
  tmp="$(mktemp)"

  SERVICE_NAME="$ecs_service" \
  TASK_DEFINITION="$task_definition" \
  DESIRED_COUNT="$desired" \
  python3 - >"$tmp" <<'PY'
import json
import os

def split_csv(value):
    return [part.strip() for part in value.replace(" ", ",").split(",") if part.strip()]

doc = {
    "cluster": os.environ["ECS_CLUSTER"],
    "serviceName": os.environ["SERVICE_NAME"],
    "taskDefinition": os.environ["TASK_DEFINITION"],
    "desiredCount": int(os.environ["DESIRED_COUNT"]),
    "launchType": os.environ["ECS_LAUNCH_TYPE"],
    "deploymentConfiguration": {
        "minimumHealthyPercent": int(os.environ.get("ECS_MIN_HEALTHY_PERCENT", "100")),
        "maximumPercent": int(os.environ.get("ECS_MAX_PERCENT", "200")),
    },
    "networkConfiguration": {
        "awsvpcConfiguration": {
            "subnets": split_csv(os.environ["ECS_SUBNETS"]),
            "securityGroups": split_csv(os.environ["ECS_SECURITY_GROUPS"]),
            "assignPublicIp": os.environ["ECS_ASSIGN_PUBLIC_IP"],
        }
    },
}

if os.environ.get("ECS_ENABLE_EXECUTE_COMMAND", "false").lower() == "true":
    doc["enableExecuteCommand"] = True

print(json.dumps(doc, separators=(",", ":")))
PY

  aws ecs create-service --region "$AWS_REGION" --cli-input-json "file://${tmp}" >/dev/null
  rm -f "$tmp"
}

for service in "${selected_services[@]}"; do
  ecs_service="$(service_name "$service")"
  desired="$(desired_count "$service")"
  task_definition="$(register_task_definition "$service")"
  status="$(service_status "$ecs_service")"

  if [[ "$status" == "ACTIVE" || "$status" == "DRAINING" ]]; then
    args=(
      ecs update-service
      --region "$AWS_REGION"
      --cluster "$ECS_CLUSTER"
      --service "$ecs_service"
      --task-definition "$task_definition"
      --force-new-deployment
    )
    if [[ "$UPDATE_DESIRED_COUNTS" == "true" ]]; then
      args+=(--desired-count "$desired")
    fi
    aws "${args[@]}" >/dev/null
    echo "Updated ECS service ${ecs_service} to ${task_definition}"
  else
    create_service "$service" "$ecs_service" "$task_definition" "$desired"
    echo "Created ECS service ${ecs_service} with desired count ${desired}"
  fi
done
