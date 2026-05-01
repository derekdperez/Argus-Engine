#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

output_file="${ECS_BOOTSTRAP_ENV_FILE:-${script_dir}/.env.generated}"
service_env_file="${SERVICE_ENV_FILE:-${script_dir}/service-env}"

metadata_token() {
  curl -fsS -X PUT "http://169.254.169.254/latest/api/token" \
    -H "X-aws-ec2-metadata-token-ttl-seconds: 21600" 2>/dev/null || true
}

metadata_get() {
  local path="$1"
  if [[ -n "${IMDS_TOKEN:-}" ]]; then
    curl -fsS -H "X-aws-ec2-metadata-token: ${IMDS_TOKEN}" "http://169.254.169.254/latest/${path}"
  else
    curl -fsS "http://169.254.169.254/latest/${path}"
  fi
}

json_field() {
  local field="$1"
  python3 -c 'import json,sys; print(json.load(sys.stdin)[sys.argv[1]])' "$field"
}

ensure_role() {
  local name="$1"
  local attach_execution_policy="$2"
  local trust tmp arn
  trust="$(mktemp)"
  cat >"$trust" <<'JSON'
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

  if ! aws iam get-role --role-name "$name" >/dev/null 2>&1; then
    aws iam create-role \
      --role-name "$name" \
      --assume-role-policy-document "file://${trust}" >/dev/null
    echo "Created IAM role ${name}" >&2
  fi
  rm -f "$trust"

  if [[ "$attach_execution_policy" == "true" ]]; then
    aws iam attach-role-policy \
      --role-name "$name" \
      --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy >/dev/null || true
  fi

  arn="$(
    aws iam get-role \
      --role-name "$name" \
      --query 'Role.Arn' \
      --output text
  )"
  echo "$arn"
}

ensure_security_group() {
  local vpc_id="$1"
  local name="${ECS_SECURITY_GROUP_NAME:-nightmare-v2-ecs-workers}"
  local sg_id
  sg_id="$(
    aws ec2 describe-security-groups \
      --region "$AWS_REGION" \
      --filters "Name=vpc-id,Values=${vpc_id}" "Name=group-name,Values=${name}" \
      --query 'SecurityGroups[0].GroupId' \
      --output text 2>/dev/null || true
  )"

  if [[ -z "$sg_id" || "$sg_id" == "None" ]]; then
    sg_id="$(
      aws ec2 create-security-group \
        --region "$AWS_REGION" \
        --group-name "$name" \
        --description "NightmareV2 ECS worker tasks" \
        --vpc-id "$vpc_id" \
        --query 'GroupId' \
        --output text
    )"
    echo "Created ECS worker security group ${sg_id}" >&2
  fi

  echo "$sg_id"
}

authorize_instance_ingress() {
  local source_sg="$1"
  shift
  local target_sgs=("$@")
  local port target_sg
  for target_sg in "${target_sgs[@]}"; do
    for port in 5432 6379 5672 15672 8080; do
      aws ec2 authorize-security-group-ingress \
        --region "$AWS_REGION" \
        --group-id "$target_sg" \
        --protocol tcp \
        --port "$port" \
        --source-group "$source_sg" >/dev/null 2>&1 || true
    done
  done
}

write_env_assignment() {
  local key="$1"
  local value="$2"
  printf '%s=%q\n' "$key" "$value" >>"$output_file"
}

IMDS_TOKEN="$(metadata_token)"
identity_json="$(metadata_get dynamic/instance-identity/document)"

AWS_REGION="${AWS_REGION:-$(printf '%s' "$identity_json" | json_field region)}"
AWS_ACCOUNT_ID="${AWS_ACCOUNT_ID:-$(aws sts get-caller-identity --query Account --output text)}"
ECS_CLUSTER="${ECS_CLUSTER:-nightmare-v2}"
ECR_PREFIX="${ECR_PREFIX:-nightmare-v2}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
ECS_LAUNCH_TYPE="${ECS_LAUNCH_TYPE:-FARGATE}"
ECS_NETWORK_MODE="${ECS_NETWORK_MODE:-awsvpc}"
# EC2 one-command mode usually runs from a public subnet without NAT/VPC endpoints.
# ENABLED keeps first-run Fargate image pulls working while service inbound remains closed by SG.
ECS_ASSIGN_PUBLIC_IP="${ECS_ASSIGN_PUBLIC_IP:-ENABLED}"
ECS_LOG_GROUP="${ECS_LOG_GROUP:-/ecs/nightmare-v2}"
ECS_LOG_PREFIX="${ECS_LOG_PREFIX:-nightmare-v2}"

instance_id="$(printf '%s' "$identity_json" | json_field instanceId)"
instance_json="$(
  aws ec2 describe-instances \
    --region "$AWS_REGION" \
    --instance-ids "$instance_id" \
    --query 'Reservations[0].Instances[0]'
)"
vpc_id="$(printf '%s' "$instance_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["VpcId"])')"
subnet_id="$(printf '%s' "$instance_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["SubnetId"])')"
private_ip="$(printf '%s' "$instance_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["PrivateIpAddress"])')"
mapfile -t instance_security_groups < <(printf '%s' "$instance_json" | python3 -c 'import json,sys; print("\n".join(g["GroupId"] for g in json.load(sys.stdin)["SecurityGroups"]))')

ECS_SUBNETS="${ECS_SUBNETS:-$subnet_id}"
if [[ -z "${ECS_SECURITY_GROUPS:-}" ]]; then
  ECS_SECURITY_GROUPS="$(ensure_security_group "$vpc_id")"
fi

authorize_instance_ingress "$ECS_SECURITY_GROUPS" "${instance_security_groups[@]}"

ECS_TASK_EXECUTION_ROLE_ARN="${ECS_TASK_EXECUTION_ROLE_ARN:-$(ensure_role "${ECS_TASK_EXECUTION_ROLE_NAME:-nightmare-v2-ecs-task-execution}" true)}"
ECS_TASK_ROLE_ARN="${ECS_TASK_ROLE_ARN:-$(ensure_role "${ECS_TASK_ROLE_NAME:-nightmare-v2-ecs-task}" false)}"

sleep "${ECS_IAM_PROPAGATION_SLEEP_SECONDS:-10}"

aws ecs create-cluster \
  --region "$AWS_REGION" \
  --cluster-name "$ECS_CLUSTER" >/dev/null 2>&1 || true
aws logs create-log-group \
  --region "$AWS_REGION" \
  --log-group-name "$ECS_LOG_GROUP" >/dev/null 2>&1 || true

if [[ ! -f "$service_env_file" || "${ECS_OVERWRITE_SERVICE_ENV:-0}" == "1" ]]; then
  cat >"$service_env_file" <<EOF
# Generated by deploy/aws/bootstrap-ecs-from-ec2.sh.
# ECS worker tasks use the EC2 host private IP to reach the self-hosted compose stack.
ConnectionStrings__Postgres=Host=${private_ip};Port=5432;Database=nightmare_v2;Username=nightmare;Password=nightmare
ConnectionStrings__FileStore=Host=${private_ip};Port=5432;Database=nightmare_v2_files;Username=nightmare;Password=nightmare
Nightmare__Postgres__MaxPoolSize=8
Nightmare__FileStore__MaxPoolSize=4
ConnectionStrings__Redis=${private_ip}:6379
RabbitMq__Host=${private_ip}
RabbitMq__Username=nightmare
RabbitMq__Password=nightmare
RabbitMq__VirtualHost=/
RabbitMq__ManagementUrl=http://${private_ip}:15672
Nightmare__ListenPlainHttp=true
Enumeration__UseSubfinder=true
Enumeration__SubfinderPath=/usr/local/bin/subfinder
Enumeration__SubfinderAllSources=true
Enumeration__SubfinderRecursive=true
Enumeration__SubfinderTimeoutSeconds=180
Enumeration__UseAmass=true
Enumeration__AmassPath=/usr/local/bin/amass
Enumeration__AmassActive=true
Enumeration__AmassBruteForce=true
Enumeration__AmassTimeoutSeconds=900
Enumeration__UseDnsFallback=true
Enumeration__DnsFallbackMaxCandidates=300
Enumeration__SubdomainWordlistPath=/opt/nightmare/wordlists/subdomains.txt
EOF
  chmod 600 "$service_env_file" 2>/dev/null || true
  echo "Wrote ECS service env file ${service_env_file}" >&2
fi

: >"$output_file"
write_env_assignment AWS_REGION "$AWS_REGION"
write_env_assignment AWS_ACCOUNT_ID "$AWS_ACCOUNT_ID"
write_env_assignment ECS_CLUSTER "$ECS_CLUSTER"
write_env_assignment ECR_PREFIX "$ECR_PREFIX"
write_env_assignment IMAGE_TAG "$IMAGE_TAG"
write_env_assignment ECS_LAUNCH_TYPE "$ECS_LAUNCH_TYPE"
write_env_assignment ECS_NETWORK_MODE "$ECS_NETWORK_MODE"
write_env_assignment ECS_SUBNETS "$ECS_SUBNETS"
write_env_assignment ECS_SECURITY_GROUPS "$ECS_SECURITY_GROUPS"
write_env_assignment ECS_ASSIGN_PUBLIC_IP "$ECS_ASSIGN_PUBLIC_IP"
write_env_assignment ECS_TASK_EXECUTION_ROLE_ARN "$ECS_TASK_EXECUTION_ROLE_ARN"
write_env_assignment ECS_TASK_ROLE_ARN "$ECS_TASK_ROLE_ARN"
write_env_assignment ECS_LOG_GROUP "$ECS_LOG_GROUP"
write_env_assignment ECS_LOG_PREFIX "$ECS_LOG_PREFIX"
write_env_assignment SERVICE_ENV_FILE "$service_env_file"
write_env_assignment COMMAND_CENTER_URL "${COMMAND_CENTER_URL:-http://${private_ip}:8080}"
write_env_assignment WORKER_SPIDER_SERVICE "${WORKER_SPIDER_SERVICE:-nightmare-worker-spider}"
write_env_assignment WORKER_ENUM_SERVICE "${WORKER_ENUM_SERVICE:-nightmare-worker-enum}"
write_env_assignment WORKER_PORTSCAN_SERVICE "${WORKER_PORTSCAN_SERVICE:-nightmare-worker-portscan}"
write_env_assignment WORKER_HIGHVALUE_SERVICE "${WORKER_HIGHVALUE_SERVICE:-nightmare-worker-highvalue}"
write_env_assignment WORKER_TECHID_SERVICE "${WORKER_TECHID_SERVICE:-nightmare-worker-techid}"

echo "Wrote ECS bootstrap env file ${output_file}" >&2
