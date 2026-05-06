#!/usr/bin/env bash
set -euo pipefail

# Bootstrap the Argus Command Center web UI onto ECS Fargate behind an ALB.
#
# Intended first run:
#   - Run from the Argus-Engine repository root.
#   - Prefer running from your current EC2 host if that host already runs
#     Postgres, Redis, and RabbitMQ via deploy/docker-compose.yml.
#
# What this creates/updates:
#   - ECR repo:              ${ECR_PREFIX}/command-center
#   - ECS cluster:           ${ECS_CLUSTER}
#   - CloudWatch log group:  ${ECS_LOG_GROUP}
#   - IAM task roles
#   - ALB + target group + HTTP listener
#   - ECS task definition + ECS service for command-center

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "Missing required command: $1" >&2
    exit 2
  }
}

need_cmd aws
need_cmd docker
need_cmd python3
need_cmd git

log() {
  printf '\n[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2
}

metadata_token() {
  curl -fsS -m 1 -X PUT "http://169.254.169.254/latest/api/token" \
    -H "X-aws-ec2-metadata-token-ttl-seconds: 21600" 2>/dev/null || true
}

metadata_get() {
  local path="$1"
  if [[ -n "${IMDS_TOKEN:-}" ]]; then
    curl -fsS -m 2 -H "X-aws-ec2-metadata-token: ${IMDS_TOKEN}" \
      "http://169.254.169.254/latest/${path}" 2>/dev/null || true
  else
    curl -fsS -m 2 "http://169.254.169.254/latest/${path}" 2>/dev/null || true
  fi
}

json_field() {
  local field="$1"
  python3 -c 'import json,sys; print(json.load(sys.stdin).get(sys.argv[1], ""))' "$field"
}

csv_to_array_json() {
  python3 - "$1" <<'PY'
import json, sys
value = sys.argv[1].replace(" ", ",")
print(json.dumps([x.strip() for x in value.split(",") if x.strip()]))
PY
}

# ---------- Detect AWS/account/network context ----------

IMDS_TOKEN="${IMDS_TOKEN:-$(metadata_token)}"
INSTANCE_ID=""
INSTANCE_REGION=""
INSTANCE_VPC_ID=""
INSTANCE_SUBNET_ID=""
INSTANCE_PRIVATE_IP=""
INSTANCE_SG_IDS=""

IDENTITY_JSON="$(metadata_get dynamic/instance-identity/document || true)"
if [[ -n "${IDENTITY_JSON}" ]]; then
  INSTANCE_ID="$(printf '%s' "${IDENTITY_JSON}" | json_field instanceId || true)"
  INSTANCE_REGION="$(printf '%s' "${IDENTITY_JSON}" | json_field region || true)"
fi

AWS_REGION="${AWS_REGION:-${INSTANCE_REGION:-$(aws configure get region 2>/dev/null || true)}}"
if [[ -z "${AWS_REGION}" ]]; then
  echo "Set AWS_REGION, for example: AWS_REGION=us-east-1 $0" >&2
  exit 2
fi
export AWS_REGION

AWS_ACCOUNT_ID="${AWS_ACCOUNT_ID:-$(aws sts get-caller-identity --query Account --output text)}"
export AWS_ACCOUNT_ID

if [[ -n "${INSTANCE_ID}" ]]; then
  INSTANCE_JSON="$(aws ec2 describe-instances \
    --region "${AWS_REGION}" \
    --instance-ids "${INSTANCE_ID}" \
    --query 'Reservations[0].Instances[0]' \
    --output json)"
  INSTANCE_VPC_ID="$(printf '%s' "${INSTANCE_JSON}" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("VpcId",""))')"
  INSTANCE_SUBNET_ID="$(printf '%s' "${INSTANCE_JSON}" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("SubnetId",""))')"
  INSTANCE_PRIVATE_IP="$(printf '%s' "${INSTANCE_JSON}" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("PrivateIpAddress",""))')"
  INSTANCE_SG_IDS="$(printf '%s' "${INSTANCE_JSON}" | python3 -c 'import json,sys; print(",".join(g["GroupId"] for g in json.load(sys.stdin).get("SecurityGroups",[])))')"
fi

VPC_ID="${VPC_ID:-${INSTANCE_VPC_ID:-}}"
if [[ -z "${VPC_ID}" ]]; then
  VPC_ID="$(aws ec2 describe-vpcs \
    --region "${AWS_REGION}" \
    --filters Name=is-default,Values=true \
    --query 'Vpcs[0].VpcId' \
    --output text 2>/dev/null || true)"
fi
if [[ -z "${VPC_ID}" || "${VPC_ID}" == "None" ]]; then
  echo "Could not detect VPC_ID. Set VPC_ID=vpc-... and re-run." >&2
  exit 2
fi

discover_subnets() {
  local vpc_id="$1"
  local public_only="${2:-true}"
  local query='Subnets[].SubnetId'
  local filters=(Name=vpc-id,Values="${vpc_id}" Name=state,Values=available)
  if [[ "${public_only}" == "true" ]]; then
    filters+=(Name=map-public-ip-on-launch,Values=true)
  fi
  aws ec2 describe-subnets \
    --region "${AWS_REGION}" \
    --filters "${filters[@]}" \
    --query "${query}" \
    --output text | tr '\t' ',' | sed 's/,$//'
}

ALB_SUBNETS="${ALB_SUBNETS:-${ECS_SUBNETS:-$(discover_subnets "${VPC_ID}" true)}}"
if [[ -z "${ALB_SUBNETS}" || "${ALB_SUBNETS}" == "None" ]]; then
  ALB_SUBNETS="$(discover_subnets "${VPC_ID}" false)"
fi
ECS_SUBNETS="${ECS_SUBNETS:-${ALB_SUBNETS}}"

ALB_SUBNET_COUNT="$(python3 - "$ALB_SUBNETS" <<'PY'
import sys
print(len([x for x in sys.argv[1].replace(" ", ",").split(",") if x.strip()]))
PY
)"
if [[ "${ALB_SUBNET_COUNT}" -lt 2 ]]; then
  cat >&2 <<EOF
The Application Load Balancer needs at least two subnets.
Detected ALB_SUBNETS=${ALB_SUBNETS:-<empty>}

Set this explicitly, for example:
  ALB_SUBNETS=subnet-aaa,subnet-bbb ECS_SUBNETS=subnet-aaa,subnet-bbb $0
EOF
  exit 2
fi

# ---------- Names/defaults ----------

ECS_CLUSTER="${ECS_CLUSTER:-argus-v2}"
ECR_PREFIX="${ECR_PREFIX:-argus-v2}"
ECR_REPOSITORY="${ECR_REPOSITORY:-${ECR_PREFIX}/command-center}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short=12 HEAD 2>/dev/null || echo latest)}"
PUSH_LATEST_TAG="${PUSH_LATEST_TAG:-1}"

ECS_SERVICE_NAME="${ECS_SERVICE_NAME:-argus-command-center}"
ECS_TASK_FAMILY="${ECS_TASK_FAMILY:-argus-v2-command-center}"
ECS_CONTAINER_NAME="${ECS_CONTAINER_NAME:-command-center}"
ECS_CPU="${ECS_CPU:-1024}"
ECS_MEMORY="${ECS_MEMORY:-2048}"
ECS_DESIRED_COUNT="${ECS_DESIRED_COUNT:-1}"
ECS_ASSIGN_PUBLIC_IP="${ECS_ASSIGN_PUBLIC_IP:-ENABLED}"
ECS_PLATFORM_VERSION="${ECS_PLATFORM_VERSION:-LATEST}"
ECS_ENABLE_EXECUTE_COMMAND="${ECS_ENABLE_EXECUTE_COMMAND:-false}"

ECS_LOG_GROUP="${ECS_LOG_GROUP:-/ecs/argus-v2}"
ECS_LOG_PREFIX="${ECS_LOG_PREFIX:-argus-v2}"

ALB_NAME="${ALB_NAME:-argus-command-center-alb}"
ALB_SG_NAME="${ALB_SG_NAME:-argus-command-center-alb-sg}"
ALB_ALLOWED_CIDR="${ALB_ALLOWED_CIDR:-0.0.0.0/0}"
TARGET_GROUP_NAME="${TARGET_GROUP_NAME:-argus-command-center-tg}"
TASK_SG_NAME="${TASK_SG_NAME:-argus-command-center-task-sg}"

ECS_TASK_EXECUTION_ROLE_NAME="${ECS_TASK_EXECUTION_ROLE_NAME:-argus-v2-ecs-task-execution}"
ECS_TASK_ROLE_NAME="${ECS_TASK_ROLE_NAME:-argus-v2-ecs-task}"

SERVICE_ENV_FILE="${SERVICE_ENV_FILE:-${SCRIPT_DIR}/ecs-command-center-service-env}"
OVERWRITE_SERVICE_ENV="${OVERWRITE_SERVICE_ENV:-0}"

CORE_HOST="${CORE_HOST:-${INSTANCE_PRIVATE_IP:-}}"
if [[ -z "${CORE_HOST}" ]]; then
  cat >&2 <<EOF
CORE_HOST was not detected. This should be the private IP/DNS that ECS tasks use to reach
Postgres, Redis, and RabbitMQ.

Example:
  CORE_HOST=10.0.1.25 VPC_ID=${VPC_ID} ALB_SUBNETS=subnet-aaa,subnet-bbb ECS_SUBNETS=subnet-aaa,subnet-bbb $0
EOF
  exit 2
fi

ARGUS_DB_NAME="${ARGUS_DB_NAME:-nightmare_v2}"
ARGUS_FILESTORE_DB_NAME="${ARGUS_FILESTORE_DB_NAME:-nightmare_v2_files}"
ARGUS_DB_USERNAME="${ARGUS_DB_USERNAME:-nightmare}"
ARGUS_DB_PASSWORD="${ARGUS_DB_PASSWORD:-nightmare}"
ARGUS_REDIS_ENDPOINT="${ARGUS_REDIS_ENDPOINT:-${CORE_HOST}:6379}"
ARGUS_RABBITMQ_HOST="${ARGUS_RABBITMQ_HOST:-${CORE_HOST}}"
ARGUS_RABBITMQ_USERNAME="${ARGUS_RABBITMQ_USERNAME:-nightmare}"
ARGUS_RABBITMQ_PASSWORD="${ARGUS_RABBITMQ_PASSWORD:-nightmare}"
ARGUS_RABBITMQ_VHOST="${ARGUS_RABBITMQ_VHOST:-/}"
ARGUS_RABBITMQ_MANAGEMENT_URL="${ARGUS_RABBITMQ_MANAGEMENT_URL:-http://${CORE_HOST}:15672}"

WAIT_FOR_STABLE="${WAIT_FOR_STABLE:-0}"

REGISTRY="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
IMAGE_URI="${REGISTRY}/${ECR_REPOSITORY}:${IMAGE_TAG}"
LATEST_IMAGE_URI="${REGISTRY}/${ECR_REPOSITORY}:latest"

# ---------- Helpers that create/reuse AWS resources ----------

ensure_ecr_repository() {
  if aws ecr describe-repositories \
      --region "${AWS_REGION}" \
      --repository-names "${ECR_REPOSITORY}" >/dev/null 2>&1; then
    log "ECR repository exists: ${ECR_REPOSITORY}"
  else
    log "Creating ECR repository: ${ECR_REPOSITORY}"
    aws ecr create-repository \
      --region "${AWS_REGION}" \
      --repository-name "${ECR_REPOSITORY}" >/dev/null
  fi
}

ensure_cluster() {
  log "Ensuring ECS cluster: ${ECS_CLUSTER}"
  aws ecs create-cluster \
    --region "${AWS_REGION}" \
    --cluster-name "${ECS_CLUSTER}" >/dev/null 2>&1 || true
}

ensure_log_group() {
  log "Ensuring CloudWatch Logs group: ${ECS_LOG_GROUP}"
  aws logs create-log-group \
    --region "${AWS_REGION}" \
    --log-group-name "${ECS_LOG_GROUP}" >/dev/null 2>&1 || true
}

ensure_role() {
  local role_name="$1"
  local attach_execution_policy="$2"
  local trust_file
  trust_file="$(mktemp)"
  cat >"${trust_file}" <<'JSON'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "Service": "ecs-tasks.amazonaws.com" },
      "Action": "sts:AssumeRole"
    }
  ]
}
JSON

  if ! aws iam get-role --role-name "${role_name}" >/dev/null 2>&1; then
    log "Creating IAM role: ${role_name}"
    aws iam create-role \
      --role-name "${role_name}" \
      --assume-role-policy-document "file://${trust_file}" >/dev/null
  else
    log "IAM role exists: ${role_name}"
  fi
  rm -f "${trust_file}"

  if [[ "${attach_execution_policy}" == "true" ]]; then
    aws iam attach-role-policy \
      --role-name "${role_name}" \
      --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy >/dev/null 2>&1 || true
  fi

  aws iam get-role \
    --role-name "${role_name}" \
    --query 'Role.Arn' \
    --output text
}

ensure_security_group() {
  local name="$1"
  local description="$2"
  local sg_id

  sg_id="$(aws ec2 describe-security-groups \
    --region "${AWS_REGION}" \
    --filters "Name=vpc-id,Values=${VPC_ID}" "Name=group-name,Values=${name}" \
    --query 'SecurityGroups[0].GroupId' \
    --output text 2>/dev/null || true)"

  if [[ -z "${sg_id}" || "${sg_id}" == "None" ]]; then
    log "Creating security group: ${name}"
    sg_id="$(aws ec2 create-security-group \
      --region "${AWS_REGION}" \
      --group-name "${name}" \
      --description "${description}" \
      --vpc-id "${VPC_ID}" \
      --query 'GroupId' \
      --output text)"
  else
    log "Security group exists: ${name} (${sg_id})"
  fi

  echo "${sg_id}"
}

authorize_ingress_cidr() {
  local sg_id="$1"
  local port="$2"
  local cidr="$3"
  aws ec2 authorize-security-group-ingress \
    --region "${AWS_REGION}" \
    --group-id "${sg_id}" \
    --protocol tcp \
    --port "${port}" \
    --cidr "${cidr}" >/dev/null 2>&1 || true
}

authorize_ingress_sg() {
  local target_sg="$1"
  local port="$2"
  local source_sg="$3"
  aws ec2 authorize-security-group-ingress \
    --region "${AWS_REGION}" \
    --group-id "${target_sg}" \
    --protocol tcp \
    --port "${port}" \
    --source-group "${source_sg}" >/dev/null 2>&1 || true
}

ensure_alb() {
  local alb_arn

  alb_arn="$(aws elbv2 describe-load-balancers \
    --region "${AWS_REGION}" \
    --names "${ALB_NAME}" \
    --query 'LoadBalancers[0].LoadBalancerArn' \
    --output text 2>/dev/null || true)"

  if [[ -z "${alb_arn}" || "${alb_arn}" == "None" ]]; then
    log "Creating ALB: ${ALB_NAME}"
    alb_arn="$(aws elbv2 create-load-balancer \
      --region "${AWS_REGION}" \
      --name "${ALB_NAME}" \
      --type application \
      --scheme internet-facing \
      --ip-address-type ipv4 \
      --security-groups "${ALB_SG_ID}" \
      --subnets $(echo "${ALB_SUBNETS}" | tr ',' ' ') \
      --query 'LoadBalancers[0].LoadBalancerArn' \
      --output text)"
    aws elbv2 wait load-balancer-available \
      --region "${AWS_REGION}" \
      --load-balancer-arns "${alb_arn}"
  else
    log "ALB exists: ${ALB_NAME}"
  fi

  echo "${alb_arn}"
}

ensure_target_group() {
  local tg_arn

  tg_arn="$(aws elbv2 describe-target-groups \
    --region "${AWS_REGION}" \
    --names "${TARGET_GROUP_NAME}" \
    --query 'TargetGroups[0].TargetGroupArn' \
    --output text 2>/dev/null || true)"

  if [[ -z "${tg_arn}" || "${tg_arn}" == "None" ]]; then
    log "Creating target group: ${TARGET_GROUP_NAME}"
    tg_arn="$(aws elbv2 create-target-group \
      --region "${AWS_REGION}" \
      --name "${TARGET_GROUP_NAME}" \
      --protocol HTTP \
      --port 8080 \
      --vpc-id "${VPC_ID}" \
      --target-type ip \
      --health-check-enabled \
      --health-check-protocol HTTP \
      --health-check-path /health/ready \
      --health-check-interval-seconds 30 \
      --health-check-timeout-seconds 5 \
      --healthy-threshold-count 2 \
      --unhealthy-threshold-count 3 \
      --matcher HttpCode=200-399 \
      --query 'TargetGroups[0].TargetGroupArn' \
      --output text)"
  else
    log "Target group exists: ${TARGET_GROUP_NAME}"
  fi

  echo "${tg_arn}"
}

ensure_http_listener() {
  local listener_arn

  listener_arn="$(aws elbv2 describe-listeners \
    --region "${AWS_REGION}" \
    --load-balancer-arn "${ALB_ARN}" \
    --query 'Listeners[?Port==`80`].ListenerArn | [0]' \
    --output text 2>/dev/null || true)"

  if [[ -z "${listener_arn}" || "${listener_arn}" == "None" ]]; then
    log "Creating HTTP listener on ALB port 80"
    aws elbv2 create-listener \
      --region "${AWS_REGION}" \
      --load-balancer-arn "${ALB_ARN}" \
      --protocol HTTP \
      --port 80 \
      --default-actions "Type=forward,TargetGroupArn=${TG_ARN}" >/dev/null
  else
    log "Updating existing HTTP listener to forward to ${TARGET_GROUP_NAME}"
    aws elbv2 modify-listener \
      --region "${AWS_REGION}" \
      --listener-arn "${listener_arn}" \
      --default-actions "Type=forward,TargetGroupArn=${TG_ARN}" >/dev/null
  fi
}

write_service_env_if_needed() {
  if [[ -f "${SERVICE_ENV_FILE}" && "${OVERWRITE_SERVICE_ENV}" != "1" ]]; then
    log "Using existing service env file: ${SERVICE_ENV_FILE}"
    return
  fi

  log "Writing service env file: ${SERVICE_ENV_FILE}"
  cat >"${SERVICE_ENV_FILE}" <<EOF
# Generated by deploy/aws/bootstrap-ecs-command-center-web.sh
# These values are placed directly in the ECS task definition.
# For production hardening, migrate passwords into AWS Secrets Manager later.

ASPNETCORE_URLS=http://+:8080
Argus__ListenPlainHttp=true
argus__ListenPlainHttp=true
Nightmare__ListenPlainHttp=true
NIGHTMARE__ListenPlainHttp=true

ConnectionStrings__Postgres=Host=${CORE_HOST};Port=5432;Database=${ARGUS_DB_NAME};Username=${ARGUS_DB_USERNAME};Password=${ARGUS_DB_PASSWORD}
ConnectionStrings__FileStore=Host=${CORE_HOST};Port=5432;Database=${ARGUS_FILESTORE_DB_NAME};Username=${ARGUS_DB_USERNAME};Password=${ARGUS_DB_PASSWORD}
ConnectionStrings__Redis=${ARGUS_REDIS_ENDPOINT}

RabbitMq__Host=${ARGUS_RABBITMQ_HOST}
RabbitMq__Username=${ARGUS_RABBITMQ_USERNAME}
RabbitMq__Password=${ARGUS_RABBITMQ_PASSWORD}
RabbitMq__VirtualHost=${ARGUS_RABBITMQ_VHOST}
RabbitMq__ManagementUrl=${ARGUS_RABBITMQ_MANAGEMENT_URL}

Nightmare__Postgres__MaxPoolSize=8
Nightmare__FileStore__MaxPoolSize=4
EOF
}

build_and_push_image() {
  log "Logging in to ECR: ${REGISTRY}"
  aws ecr get-login-password --region "${AWS_REGION}" \
    | docker login --username AWS --password-stdin "${REGISTRY}" >/dev/null

  log "Building Docker image: ${IMAGE_URI}"
  DOCKER_BUILDKIT="${DOCKER_BUILDKIT:-1}" docker build \
    -f deploy/Dockerfile.web \
    --build-arg BUILD_SOURCE_STAMP="$(git rev-parse HEAD 2>/dev/null || echo unknown)" \
    --build-arg COMPONENT_VERSION="${COMPONENT_VERSION:-${IMAGE_TAG}}" \
    -t "${IMAGE_URI}" \
    .

  log "Pushing Docker image: ${IMAGE_URI}"
  docker push "${IMAGE_URI}"

  if [[ "${PUSH_LATEST_TAG}" == "1" ]]; then
    log "Tagging and pushing: ${LATEST_IMAGE_URI}"
    docker tag "${IMAGE_URI}" "${LATEST_IMAGE_URI}"
    docker push "${LATEST_IMAGE_URI}"
  fi
}

register_task_definition() {
  local task_json
  task_json="$(mktemp)"

  SERVICE_ENV_FILE="${SERVICE_ENV_FILE}" \
  IMAGE_URI="${IMAGE_URI}" \
  ECS_TASK_FAMILY="${ECS_TASK_FAMILY}" \
  ECS_CONTAINER_NAME="${ECS_CONTAINER_NAME}" \
  ECS_CPU="${ECS_CPU}" \
  ECS_MEMORY="${ECS_MEMORY}" \
  ECS_TASK_EXECUTION_ROLE_ARN="${ECS_TASK_EXECUTION_ROLE_ARN}" \
  ECS_TASK_ROLE_ARN="${ECS_TASK_ROLE_ARN}" \
  ECS_LOG_GROUP="${ECS_LOG_GROUP}" \
  ECS_LOG_PREFIX="${ECS_LOG_PREFIX}" \
  AWS_REGION="${AWS_REGION}" \
  python3 - >"${task_json}" <<'PY'
import json
import os

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

env = parse_env(os.environ["SERVICE_ENV_FILE"])
environment = [{"name": key, "value": value} for key, value in sorted(env.items())]

container = {
    "name": os.environ["ECS_CONTAINER_NAME"],
    "image": os.environ["IMAGE_URI"],
    "essential": True,
    "portMappings": [
        {
            "containerPort": 8080,
            "hostPort": 8080,
            "protocol": "tcp"
        }
    ],
    "environment": environment,
    "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
            "awslogs-group": os.environ["ECS_LOG_GROUP"],
            "awslogs-region": os.environ["AWS_REGION"],
            "awslogs-stream-prefix": os.environ["ECS_LOG_PREFIX"]
        }
    },
    "healthCheck": {
        "command": ["CMD-SHELL", "curl -fsS http://127.0.0.1:8080/health/ready >/dev/null || exit 1"],
        "interval": 30,
        "timeout": 5,
        "retries": 3,
        "startPeriod": 90
    }
}

task = {
    "family": os.environ["ECS_TASK_FAMILY"],
    "networkMode": "awsvpc",
    "requiresCompatibilities": ["FARGATE"],
    "cpu": str(os.environ["ECS_CPU"]),
    "memory": str(os.environ["ECS_MEMORY"]),
    "executionRoleArn": os.environ["ECS_TASK_EXECUTION_ROLE_ARN"],
    "containerDefinitions": [container]
}

task_role = os.environ.get("ECS_TASK_ROLE_ARN", "").strip()
if task_role:
    task["taskRoleArn"] = task_role

print(json.dumps(task, indent=2))
PY

  log "Registering ECS task definition: ${ECS_TASK_FAMILY}"
  local task_def_arn
  task_def_arn="$(aws ecs register-task-definition \
    --region "${AWS_REGION}" \
    --cli-input-json "file://${task_json}" \
    --query 'taskDefinition.taskDefinitionArn' \
    --output text)"
  rm -f "${task_json}"

  echo "${task_def_arn}"
}

service_status() {
  aws ecs describe-services \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --services "${ECS_SERVICE_NAME}" \
    --query 'services[0].status' \
    --output text 2>/dev/null || true
}

create_or_update_service() {
  local status
  status="$(service_status)"

  if [[ "${status}" == "ACTIVE" ]]; then
    log "Updating ECS service: ${ECS_SERVICE_NAME}"
    aws ecs update-service \
      --region "${AWS_REGION}" \
      --cluster "${ECS_CLUSTER}" \
      --service "${ECS_SERVICE_NAME}" \
      --task-definition "${TASK_DEFINITION_ARN}" \
      --desired-count "${ECS_DESIRED_COUNT}" \
      --force-new-deployment \
      --deployment-configuration "deploymentCircuitBreaker={enable=true,rollback=true},maximumPercent=200,minimumHealthyPercent=100" >/dev/null
    return
  fi

  local service_json
  service_json="$(mktemp)"

  ECS_CLUSTER="${ECS_CLUSTER}" \
  ECS_SERVICE_NAME="${ECS_SERVICE_NAME}" \
  TASK_DEFINITION_ARN="${TASK_DEFINITION_ARN}" \
  ECS_DESIRED_COUNT="${ECS_DESIRED_COUNT}" \
  ECS_SUBNETS_JSON="$(csv_to_array_json "${ECS_SUBNETS}")" \
  ECS_SECURITY_GROUPS_JSON="$(csv_to_array_json "${TASK_SG_ID}")" \
  ECS_ASSIGN_PUBLIC_IP="${ECS_ASSIGN_PUBLIC_IP}" \
  ECS_PLATFORM_VERSION="${ECS_PLATFORM_VERSION}" \
  ECS_ENABLE_EXECUTE_COMMAND="${ECS_ENABLE_EXECUTE_COMMAND}" \
  ECS_CONTAINER_NAME="${ECS_CONTAINER_NAME}" \
  TG_ARN="${TG_ARN}" \
  python3 - >"${service_json}" <<'PY'
import json
import os

doc = {
    "cluster": os.environ["ECS_CLUSTER"],
    "serviceName": os.environ["ECS_SERVICE_NAME"],
    "taskDefinition": os.environ["TASK_DEFINITION_ARN"],
    "desiredCount": int(os.environ["ECS_DESIRED_COUNT"]),
    "launchType": "FARGATE",
    "platformVersion": os.environ["ECS_PLATFORM_VERSION"],
    "healthCheckGracePeriodSeconds": 180,
    "deploymentConfiguration": {
        "minimumHealthyPercent": 100,
        "maximumPercent": 200,
        "deploymentCircuitBreaker": {
            "enable": True,
            "rollback": True
        }
    },
    "networkConfiguration": {
        "awsvpcConfiguration": {
            "subnets": json.loads(os.environ["ECS_SUBNETS_JSON"]),
            "securityGroups": json.loads(os.environ["ECS_SECURITY_GROUPS_JSON"]),
            "assignPublicIp": os.environ["ECS_ASSIGN_PUBLIC_IP"]
        }
    },
    "loadBalancers": [
        {
            "targetGroupArn": os.environ["TG_ARN"],
            "containerName": os.environ["ECS_CONTAINER_NAME"],
            "containerPort": 8080
        }
    ]
}

if os.environ.get("ECS_ENABLE_EXECUTE_COMMAND", "false").lower() == "true":
    doc["enableExecuteCommand"] = True

print(json.dumps(doc, indent=2))
PY

  log "Creating ECS service: ${ECS_SERVICE_NAME}"
  aws ecs create-service \
    --region "${AWS_REGION}" \
    --cli-input-json "file://${service_json}" >/dev/null
  rm -f "${service_json}"
}

print_summary() {
  local alb_dns
  alb_dns="$(aws elbv2 describe-load-balancers \
    --region "${AWS_REGION}" \
    --load-balancer-arns "${ALB_ARN}" \
    --query 'LoadBalancers[0].DNSName' \
    --output text)"

  cat <<EOF

Done.

Argus Command Center URL:
  http://${alb_dns}

Useful checks:
  aws ecs describe-services --region ${AWS_REGION} --cluster ${ECS_CLUSTER} --services ${ECS_SERVICE_NAME}
  aws ecs list-tasks --region ${AWS_REGION} --cluster ${ECS_CLUSTER} --service-name ${ECS_SERVICE_NAME}
  aws logs tail ${ECS_LOG_GROUP} --region ${AWS_REGION} --follow

Important generated file:
  ${SERVICE_ENV_FILE}

EOF
}

# ---------- Run ----------

log "AWS account: ${AWS_ACCOUNT_ID}"
log "AWS region:  ${AWS_REGION}"
log "VPC:         ${VPC_ID}"
log "ALB subnets: ${ALB_SUBNETS}"
log "ECS subnets: ${ECS_SUBNETS}"
log "Core host:   ${CORE_HOST}"

ensure_ecr_repository
ensure_cluster
ensure_log_group

log "Ensuring IAM roles"
ECS_TASK_EXECUTION_ROLE_ARN="${ECS_TASK_EXECUTION_ROLE_ARN:-$(ensure_role "${ECS_TASK_EXECUTION_ROLE_NAME}" true)}"
ECS_TASK_ROLE_ARN="${ECS_TASK_ROLE_ARN:-$(ensure_role "${ECS_TASK_ROLE_NAME}" false)}"
export ECS_TASK_EXECUTION_ROLE_ARN ECS_TASK_ROLE_ARN

# IAM role propagation can lag immediately after role creation.
sleep "${IAM_PROPAGATION_SLEEP_SECONDS:-10}"

log "Ensuring security groups"
ALB_SG_ID="$(ensure_security_group "${ALB_SG_NAME}" "Argus Command Center public ALB")"
TASK_SG_ID="$(ensure_security_group "${TASK_SG_NAME}" "Argus Command Center ECS tasks")"

authorize_ingress_cidr "${ALB_SG_ID}" 80 "${ALB_ALLOWED_CIDR}"
authorize_ingress_sg "${TASK_SG_ID}" 8080 "${ALB_SG_ID}"

if [[ -n "${INSTANCE_SG_IDS}" ]]; then
  log "Allowing ECS task SG to reach current EC2 host backends"
  IFS=',' read -r -a ec2_sgs <<< "${INSTANCE_SG_IDS}"
  for sg in "${ec2_sgs[@]}"; do
    [[ -z "${sg}" ]] && continue
    for port in 5432 6379 5672 15672; do
      authorize_ingress_sg "${sg}" "${port}" "${TASK_SG_ID}"
    done
  done
else
  log "Not running on EC2 or EC2 SGs unavailable; skipping backend SG ingress automation"
fi

ALB_ARN="$(ensure_alb)"
TG_ARN="$(ensure_target_group)"
ensure_http_listener

write_service_env_if_needed
build_and_push_image

TASK_DEFINITION_ARN="$(register_task_definition)"
create_or_update_service

if [[ "${WAIT_FOR_STABLE}" == "1" ]]; then
  log "Waiting for ECS service to become stable"
  aws ecs wait services-stable \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --services "${ECS_SERVICE_NAME}"
fi

print_summary
