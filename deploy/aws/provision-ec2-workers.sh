#!/usr/bin/env bash
# Provision 2 new EC2 instances with 10 HTTP request workers each.
# Requirements: AWS CLI configured, IAM role/permissions for EC2/Security Groups/VPC
#
# Usage:
#   set -a
#   . deploy/aws/.env
#   set +a
#   deploy/aws/provision-ec2-workers.sh
#
# Environment variables required:
#   AWS_REGION              AWS region (e.g., us-east-1)
#   WORKER_INSTANCE_TYPE    Worker instance type (m7i-flex.large)
#   WORKER_KEY_PAIR         EC2 key pair name (kp1)
#   WORKER_IAM_ROLE         IAM instance profile name for workers
#   WORKER_SECURITY_GROUP   Security group name (default)
#   WORKER_COUNT            Number of workers per instance (10)
#   INSTANCE_COUNT          Number of instances to launch (2)

set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${WORKER_INSTANCE_TYPE:=m7i-flex.large}"
: "${WORKER_KEY_PAIR:=kp1}"
: "${WORKER_IAM_ROLE:=ec2-iam}"
: "${WORKER_SECURITY_GROUP:=default}"
: "${WORKER_COUNT:=10}"
: "${INSTANCE_COUNT:=2}"

# Get the default VPC
DEFAULT_VPC_ID=$(aws ec2 describe-vpcs \
  --region "$AWS_REGION" \
  --filters "Name=isDefault,Values=true" \
  --query 'Vpcs[0].VpcId' \
  --output text)

if [[ "$DEFAULT_VPC_ID" == "None" || -z "$DEFAULT_VPC_ID" ]]; then
  echo "Error: Could not find default VPC in $AWS_REGION" >&2
  exit 1
fi

echo "Using default VPC: $DEFAULT_VPC_ID"

# Get security group ID for the default security group
SG_ID=$(aws ec2 describe-security-groups \
  --region "$AWS_REGION" \
  --filters "Name=group-name,Values=$WORKER_SECURITY_GROUP" "Name=vpc-id,Values=$DEFAULT_VPC_ID" \
  --query 'SecurityGroups[0].GroupId' \
  --output text 2>/dev/null || true)

if [[ "$SG_ID" == "None" || -z "$SG_ID" ]]; then
  echo "Error: Could not find security group '$WORKER_SECURITY_GROUP' in VPC $DEFAULT_VPC_ID" >&2
  exit 1
fi

echo "Using security group: $SG_ID"

# Get a default subnet in the VPC
DEFAULT_SUBNET=$(aws ec2 describe-subnets \
  --region "$AWS_REGION" \
  --filters "Name=vpc-id,Values=$DEFAULT_VPC_ID" \
  --query 'Subnets[0].SubnetId' \
  --output text 2>/dev/null || true)

if [[ "$DEFAULT_SUBNET" == "None" || -z "$DEFAULT_SUBNET" ]]; then
  DEFAULT_SUBNET=""
fi

# Get the latest Ubuntu 24.04 LTS AMI
UBUNTU_AMI=$(aws ec2 describe-images \
  --region "$AWS_REGION" \
  --owners 099720109477 \
  --filters "Name=name,Values=ubuntu/images/hvm-ssd/ubuntu-noble-24.04-amd64-server-*" \
  --query 'sort_by(Images, &CreationDate)[-1].ImageId' \
  --output text 2>/dev/null || echo "ami-0e8d59ad6e1fef3d2")

if [[ "$UBUNTU_AMI" == "None" || -z "$UBUNTU_AMI" ]]; then
  UBUNTU_AMI="ami-0e8d59ad6e1fef3d2"  # Fallback
fi

echo "Using AMI: $UBUNTU_AMI (Ubuntu 24.04 LTS)"

# User data script to bootstrap instances
read -r -d '' USER_DATA_SCRIPT <<'EOF' || true
#!/bin/bash
set -euo pipefail

# Update system and install dependencies
apt-get update
apt-get install -y \
  curl wget git jq ca-certificates gnupg lsb-release \
  apt-transport-https software-properties-common

# Install Docker from official Docker repository
mkdir -p /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Enable Docker daemon
systemctl enable docker
systemctl start docker

# Add ubuntu user to docker group for script operations
usermod -aG docker ubuntu || true

# Clone or update the NightmareV2 repository
if [ ! -d /opt/nightmare ]; then
  git clone https://github.com/derekdperez/NightmareV2.git /opt/nightmare
  cd /opt/nightmare
  git checkout main
else
  cd /opt/nightmare
  git fetch origin
  git checkout main
  git pull origin main
fi

# Create provisioning log
INSTANCE_ID=$(ec2-metadata --instance-id | cut -d ' ' -f 2 || curl -s http://169.254.169.254/latest/meta-data/instance-id)
INSTANCE_IP=$(ec2-metadata --local-ipv4 | cut -d ' ' -f 2 || curl -s http://169.254.169.254/latest/meta-data/local-ipv4)

echo "Instance provisioned: $INSTANCE_ID" > /opt/nightmare/PROVISION_LOG.txt
echo "Private IP: $INSTANCE_IP" >> /opt/nightmare/PROVISION_LOG.txt
echo "Timestamp: $(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> /opt/nightmare/PROVISION_LOG.txt

chmod -R 777 /opt/nightmare
EOF

# Base64-encode the user data script
USER_DATA_B64=$(printf '%s' "$USER_DATA_SCRIPT" | base64 -w0)

echo "Launching $INSTANCE_COUNT EC2 instances..."
echo ""

# Launch instances
INSTANCE_IDS=()
INSTANCE_PRIVATE_IPS=()

for ((i=1; i<=INSTANCE_COUNT; i++)); do
  INSTANCE_NAME="nightmare-worker-$i"
  
  echo "Launching instance $i of $INSTANCE_COUNT: $INSTANCE_NAME"
  
  # Build the launch command
  LAUNCH_CMD="aws ec2 run-instances \
    --region '$AWS_REGION' \
    --image-id '$UBUNTU_AMI' \
    --instance-type '$WORKER_INSTANCE_TYPE' \
    --key-name '$WORKER_KEY_PAIR' \
    --security-group-ids '$SG_ID' \
    --user-data '$USER_DATA_B64' \
    --tag-specifications 'ResourceType=instance,Tags=[{Key=Name,Value=$INSTANCE_NAME},{Key=Purpose,Value=nightmare-worker}]' \
    --output json"
  
  # Add subnet if available
  if [[ -n "$DEFAULT_SUBNET" ]]; then
    LAUNCH_CMD="$LAUNCH_CMD --subnet-id '$DEFAULT_SUBNET'"
  fi
  
  # Add IAM role if available
  if [[ -n "$WORKER_IAM_ROLE" ]]; then
    LAUNCH_CMD="$LAUNCH_CMD --iam-instance-profile 'Name=$WORKER_IAM_ROLE'" 2>/dev/null || true
  fi
  
  # Execute the launch command
  LAUNCH_OUTPUT=$(eval "$LAUNCH_CMD" 2>/dev/null || aws ec2 run-instances \
    --region "$AWS_REGION" \
    --image-id "$UBUNTU_AMI" \
    --instance-type "$WORKER_INSTANCE_TYPE" \
    --key-name "$WORKER_KEY_PAIR" \
    --security-group-ids "$SG_ID" \
    --user-data "$USER_DATA_B64" \
    --tag-specifications "ResourceType=instance,Tags=[{Key=Name,Value=$INSTANCE_NAME}]" \
    --output json)
  
  INSTANCE_ID=$(echo "$LAUNCH_OUTPUT" | jq -r '.Instances[0].InstanceId')
  PRIVATE_IP=$(echo "$LAUNCH_OUTPUT" | jq -r '.Instances[0].PrivateIpAddress')
  
  INSTANCE_IDS+=("$INSTANCE_ID")
  INSTANCE_PRIVATE_IPS+=("$PRIVATE_IP")
  
  echo "  Instance ID: $INSTANCE_ID"
  echo "  Private IP: $PRIVATE_IP"
done

echo ""
echo "======================================"
echo "EC2 Instance Provisioning Complete"
echo "======================================"
echo "Launched ${#INSTANCE_IDS[@]} instances:"
for ((i=0; i<${#INSTANCE_IDS[@]}; i++)); do
  echo "  [$((i+1))] ${INSTANCE_IDS[$i]} (IP: ${INSTANCE_PRIVATE_IPS[$i]})"
done

echo ""
echo "Next steps:"
echo "1. Wait for instances to reach 'running' state (~1-2 minutes):"
echo "   watch -n 2 aws ec2 describe-instances --region $AWS_REGION --instance-ids ${INSTANCE_IDS[*]} --query 'Reservations[].Instances[].[Tags[?Key==\`Name\`].Value[],State.Name,PrivateIpAddress]' --output table"
echo ""
echo "2. Once instances are running, deploy 10 workers to each instance:"
echo "   deploy/aws/deploy-worker-instances.sh ${INSTANCE_IDS[*]}"
echo ""
echo "3. Verify HTTP workers are running:"
echo "   curl http://\${COMMAND_CENTER}/api/http-request-queue/metrics"
