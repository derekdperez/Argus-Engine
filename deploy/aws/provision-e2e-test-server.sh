#!/usr/bin/env bash
# Ensure a long-lived EC2 host exists for E2E testing.
#
# The host is intentionally reused between builds. Per-run freshness is handled
# by src/tests/e2e/reset-e2e-database.sh, which recreates the databases before
# tests execute.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ -f "${script_dir}/.env" ]]; then
  set -a
  # shellcheck source=/dev/null
  . "${script_dir}/.env"
  set +a
fi

: "${AWS_REGION:?Set AWS_REGION}"
: "${E2E_INSTANCE_NAME:=argus-e2e-test-server}"
: "${E2E_INSTANCE_TYPE:=${EC2_WORKER_INSTANCE_TYPE:-${WORKER_INSTANCE_TYPE:-m7i-flex.large}}}"
: "${E2E_KEY_PAIR:=${EC2_WORKER_KEY_PAIR:-${WORKER_KEY_PAIR:-}}}"
: "${E2E_IAM_INSTANCE_PROFILE:=${EC2_WORKER_IAM_ROLE:-${WORKER_IAM_ROLE:-}}}"
: "${E2E_SECURITY_GROUP:=${EC2_WORKER_SECURITY_GROUP:-${WORKER_SECURITY_GROUP:-default}}}"
: "${E2E_SUBNET_ID:=${EC2_WORKER_SUBNET_ID:-}}"
: "${E2E_REPOSITORY_URL:=${EC2_WORKER_REPOSITORY_URL:-https://github.com/derekdperez/argusV2.git}}"
: "${E2E_GIT_BRANCH:=${EC2_WORKER_GIT_BRANCH:-main}}"

find_instance() {
  aws ec2 describe-instances \
    --region "$AWS_REGION" \
    --filters \
      "Name=tag:Name,Values=${E2E_INSTANCE_NAME}" \
      "Name=tag:Purpose,Values=argus-e2e-test-server" \
      "Name=instance-state-name,Values=pending,running,stopping,stopped" \
    --query 'Reservations[].Instances[] | sort_by(@, &LaunchTime)[-1].InstanceId' \
    --output text
}

instance_id="$(find_instance)"
if [[ "$instance_id" != "None" && -n "$instance_id" ]]; then
  state="$(aws ec2 describe-instances \
    --region "$AWS_REGION" \
    --instance-ids "$instance_id" \
    --query 'Reservations[0].Instances[0].State.Name' \
    --output text)"

  if [[ "$state" == "stopped" ]]; then
    echo "Starting stopped E2E EC2 instance ${instance_id}..."
    aws ec2 start-instances --region "$AWS_REGION" --instance-ids "$instance_id" >/dev/null
  else
    echo "Reusing E2E EC2 instance ${instance_id} (${state})."
  fi

  echo "$instance_id"
  exit 0
fi

if [[ -z "$E2E_SUBNET_ID" ]]; then
  default_vpc_id="$(aws ec2 describe-vpcs \
    --region "$AWS_REGION" \
    --filters "Name=isDefault,Values=true" \
    --query 'Vpcs[0].VpcId' \
    --output text)"

  if [[ "$default_vpc_id" == "None" || -z "$default_vpc_id" ]]; then
    echo "E2E_SUBNET_ID is required because no default VPC was found in ${AWS_REGION}." >&2
    exit 2
  fi

  E2E_SUBNET_ID="$(aws ec2 describe-subnets \
    --region "$AWS_REGION" \
    --filters "Name=vpc-id,Values=${default_vpc_id}" \
    --query 'Subnets[0].SubnetId' \
    --output text)"
fi

if [[ "$E2E_SECURITY_GROUP" == sg-* ]]; then
  security_group_id="$E2E_SECURITY_GROUP"
else
  vpc_id="$(aws ec2 describe-subnets \
    --region "$AWS_REGION" \
    --subnet-ids "$E2E_SUBNET_ID" \
    --query 'Subnets[0].VpcId' \
    --output text)"
  security_group_id="$(aws ec2 describe-security-groups \
    --region "$AWS_REGION" \
    --filters "Name=group-name,Values=${E2E_SECURITY_GROUP}" "Name=vpc-id,Values=${vpc_id}" \
    --query 'SecurityGroups[0].GroupId' \
    --output text)"
fi

if [[ "$security_group_id" == "None" || -z "$security_group_id" ]]; then
  echo "Could not resolve E2E security group '${E2E_SECURITY_GROUP}'." >&2
  exit 2
fi

ami_id="${E2E_AMI_ID:-}"
if [[ -z "$ami_id" ]]; then
  ami_id="$(aws ec2 describe-images \
    --region "$AWS_REGION" \
    --owners 099720109477 \
    --filters "Name=name,Values=ubuntu/images/hvm-ssd/ubuntu-noble-24.04-amd64-server-*" \
    --query 'sort_by(Images, &CreationDate)[-1].ImageId' \
    --output text)"
fi

if [[ "$ami_id" == "None" || -z "$ami_id" ]]; then
  echo "Could not resolve an Ubuntu 24.04 AMI. Set E2E_AMI_ID." >&2
  exit 2
fi

user_data_file="$(mktemp)"
cat >"$user_data_file" <<EOF
#!/bin/bash
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y curl git jq ca-certificates gnupg lsb-release snapd

if ! command -v amazon-ssm-agent >/dev/null 2>&1; then
  snap install amazon-ssm-agent --classic || true
fi
systemctl enable snap.amazon-ssm-agent.amazon-ssm-agent.service >/dev/null 2>&1 || true
systemctl start snap.amazon-ssm-agent.amazon-ssm-agent.service >/dev/null 2>&1 || true

if ! command -v docker >/dev/null 2>&1; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  chmod a+r /etc/apt/keyrings/docker.gpg
  echo "deb [arch=\$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \$(. /etc/os-release && echo "\$VERSION_CODENAME") stable" >/etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi

systemctl enable docker
systemctl start docker
usermod -aG docker ubuntu || true

if [[ ! -d /opt/argus/.git ]]; then
  rm -rf /opt/argus
  git clone "${E2E_REPOSITORY_URL}" /opt/argus
fi

cd /opt/argus
git fetch origin "${E2E_GIT_BRANCH}" || true
git checkout "${E2E_GIT_BRANCH}" || true
git pull --ff-only origin "${E2E_GIT_BRANCH}" || true
chown -R ubuntu:ubuntu /opt/argus

cat >/opt/argus/E2E_SERVER.txt <<MARKER
name=${E2E_INSTANCE_NAME}
repo=${E2E_REPOSITORY_URL}
branch=${E2E_GIT_BRANCH}
created_at_utc=\$(date -u +%Y-%m-%dT%H:%M:%SZ)
MARKER
EOF

run_args=(
  --region "$AWS_REGION"
  --image-id "$ami_id"
  --instance-type "$E2E_INSTANCE_TYPE"
  --subnet-id "$E2E_SUBNET_ID"
  --security-group-ids "$security_group_id"
  --user-data "file://${user_data_file}"
  --tag-specifications "ResourceType=instance,Tags=[{Key=Name,Value=${E2E_INSTANCE_NAME}},{Key=Purpose,Value=argus-e2e-test-server}]"
  --output json
)

if [[ -n "$E2E_KEY_PAIR" ]]; then
  run_args+=(--key-name "$E2E_KEY_PAIR")
fi

if [[ -n "$E2E_IAM_INSTANCE_PROFILE" ]]; then
  run_args+=(--iam-instance-profile "Name=${E2E_IAM_INSTANCE_PROFILE}")
fi

echo "Provisioning E2E EC2 instance ${E2E_INSTANCE_NAME}..."
launch_output="$(aws ec2 run-instances "${run_args[@]}")"
rm -f "$user_data_file"

instance_id="$(printf '%s' "$launch_output" | jq -r '.Instances[0].InstanceId')"
echo "$instance_id"
