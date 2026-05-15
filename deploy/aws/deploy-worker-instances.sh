#!/usr/bin/env bash
# Deploy and start 10 HTTP request worker containers on specified EC2 instances.
#
# Usage:
#   # Deploy to specific instances
#   deploy/aws/deploy-worker-instances.sh i-0123456789abcdef0 i-0123456789abcdef1
#
#   # Or use comma-separated IDs
#   deploy/aws/deploy-worker-instances.sh i-0123456789abcdef0,i-0123456789abcdef1
#
# Environment variables required:
#   AWS_REGION              AWS region (e.g., us-east-1)
#   WORKER_COUNT            Number of workers per instance (default: 10)
#   SSH_KEY_PATH            Path to EC2 key pair PEM file (e.g., ~/.ssh/kp1.pem)
#   SSH_USER                SSH user for EC2 instances (default: ubuntu)

set -euo pipefail

: "${AWS_REGION:?Set AWS_REGION}"
: "${WORKER_COUNT:=10}"
: "${SSH_USER:=ubuntu}"

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <instance-id-1> [<instance-id-2> ...]" >&2
  echo "   or: $0 <instance-id-1>,<instance-id-2>,..." >&2
  exit 1
fi

# Parse instance IDs (handle both space-separated and comma-separated)
INSTANCE_IDS=()
for arg in "$@"; do
  IFS=',' read -ra PARTS <<< "$arg"
  for part in "${PARTS[@]}"; do
    INSTANCE_IDS+=("${part// /}")  # Trim whitespace
  done
done

if [[ ${#INSTANCE_IDS[@]} -eq 0 ]]; then
  echo "Error: No instance IDs provided" >&2
  exit 1
fi

echo "Deploying $WORKER_COUNT workers to ${#INSTANCE_IDS[@]} instance(s)..."
echo "Instance IDs: ${INSTANCE_IDS[*]}"
echo ""

# Function to deploy workers to a single instance
deploy_to_instance() {
  local instance_id="$1"
  local idx="$2"
  
  echo "[$idx/${#INSTANCE_IDS[@]}] Deploying to instance $instance_id..."
  
  # Get instance details
  local instance_info=$(aws ec2 describe-instances \
    --region "$AWS_REGION" \
    --instance-ids "$instance_id" \
    --query 'Reservations[0].Instances[0].[PublicIpAddress,PrivateIpAddress,State.Name]' \
    --output text)
  
  read -r PUBLIC_IP PRIVATE_IP STATE <<< "$instance_info"
  
  if [[ "$STATE" != "running" ]]; then
    echo "  Warning: Instance is in state '$STATE', not 'running'. Skipping." >&2
    return 1
  fi
  
  # Use SSH with connection timeout and disable strict host key checking
  local SSH_OPTS="-o ConnectTimeout=30 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  
  # Try public IP first, then private IP
  local SSH_TARGET="$PUBLIC_IP"
  if [[ "$PUBLIC_IP" == "None" || -z "$PUBLIC_IP" ]]; then
    SSH_TARGET="$PRIVATE_IP"
    echo "  Using private IP: $SSH_TARGET"
  else
    echo "  Using public IP: $SSH_TARGET"
  fi
  
  # Format SSH command with error handling
  local ssh_cmd="ssh $SSH_OPTS -i '${SSH_KEY_PATH}' ${SSH_USER}@${SSH_TARGET}"
  
  # Pull latest code
  echo "  [1/5] Pulling latest code..."
  $ssh_cmd 'cd /opt/argus && git fetch origin && git checkout main && git pull origin main' 2>&1 | grep -v "^warning:" || true
  
  # Ensure Docker is running
  echo "  [2/5] Ensuring Docker is running..."
  $ssh_cmd 'sudo systemctl is-active docker > /dev/null || sudo systemctl start docker' || true
  
  # Stop existing workers
  echo "  [3/5] Stopping existing workers..."
  $ssh_cmd 'cd /opt/argus && docker compose -f deploy/docker-compose.yml down worker-spider 2>/dev/null' || true
  
  # Build the worker image
  echo "  [4/5] Building worker image..."
  $ssh_cmd 'cd /opt/argus && COMPOSE_BAKE=false docker compose -f deploy/docker-compose.yml build worker-spider 2>&1' | tail -20
  
  # Deploy workers with replicas
  echo "  [5/5] Starting $WORKER_COUNT worker containers..."
  $ssh_cmd "cd /opt/argus && docker compose -f deploy/docker-compose.yml up -d worker-spider --scale worker-spider=$WORKER_COUNT" 2>&1
  
  # Verify workers are running
  sleep 3
  local running_count=$($ssh_cmd "docker ps -q -f 'label=com.docker.compose.service=worker-spider' | wc -l" 2>/dev/null || echo "0")
  
  if [[ "$running_count" -eq "$WORKER_COUNT" ]]; then
    echo "  ✓ Successfully deployed $running_count/$WORKER_COUNT workers"
  else
    echo "  ⚠ Warning: Only $running_count/$WORKER_COUNT workers running (may still be starting)"
  fi
  
  return 0
}

# Deploy to each instance
FAILED_INSTANCES=()
for ((i=0; i<${#INSTANCE_IDS[@]}; i++)); do
  instance_id="${INSTANCE_IDS[$i]}"
  if ! deploy_to_instance "$instance_id" "$((i+1))"; then
    FAILED_INSTANCES+=("$instance_id")
  fi
  echo ""
done

echo "======================================"
echo "Deployment Summary"
echo "======================================"
echo "Total instances: ${#INSTANCE_IDS[@]}"
echo "Successful: $((${#INSTANCE_IDS[@]} - ${#FAILED_INSTANCES[@]}))"
if [[ ${#FAILED_INSTANCES[@]} -gt 0 ]]; then
  echo "Failed: ${#FAILED_INSTANCES[@]}"
  echo "  Failed instances: ${FAILED_INSTANCES[*]}"
fi

echo ""
echo "Next steps:"
echo "1. Wait a few seconds for workers to initialize and connect to RabbitMQ"
echo "2. Check HTTP request queue metrics:"
echo "   curl http://\${COMMAND_CENTER_URL}/api/http-request-queue/metrics"
echo "3. Monitor worker logs:"
echo "   aws ec2-instance-connect send-command --instance-ids ${INSTANCE_IDS[0]} --document-name 'AWS-RunShellScript' --parameters 'commands=[\"cd /opt/argus && docker compose -f deploy/docker-compose.yml logs -f worker-spider\"]'"

if [[ ${#FAILED_INSTANCES[@]} -gt 0 ]]; then
  exit 1
fi
