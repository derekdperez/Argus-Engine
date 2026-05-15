#!/usr/bin/env bash
set -euo pipefail

# Print current status for the Argus Command Center ECS web service.

AWS_REGION="${AWS_REGION:-$(aws configure get region 2>/dev/null || true)}"
if [[ -z "${AWS_REGION}" ]]; then
  echo "Set AWS_REGION, for example: AWS_REGION=us-east-1 $0" >&2
  exit 2
fi

ECS_CLUSTER="${ECS_CLUSTER:-argus-v2}"
ECS_SERVICE_NAME="${ECS_SERVICE_NAME:-argus-command-center}"
ECS_LOG_GROUP="${ECS_LOG_GROUP:-/ecs/argus-v2}"
ALB_NAME="${ALB_NAME:-argus-command-center-alb}"

echo "== ECS service =="
aws ecs describe-services \
  --region "${AWS_REGION}" \
  --cluster "${ECS_CLUSTER}" \
  --services "${ECS_SERVICE_NAME}" \
  --query 'services[0].{status:status,desired:desiredCount,running:runningCount,pending:pendingCount,taskDefinition:taskDefinition,rollout:deployments[0].rolloutState,reason:deployments[0].rolloutStateReason}' \
  --output table || true

echo
echo "== Recent ECS service events =="
aws ecs describe-services \
  --region "${AWS_REGION}" \
  --cluster "${ECS_CLUSTER}" \
  --services "${ECS_SERVICE_NAME}" \
  --query 'services[0].events[0:10].[createdAt,message]' \
  --output table || true

echo
echo "== Tasks =="
TASK_ARNS="$(aws ecs list-tasks \
  --region "${AWS_REGION}" \
  --cluster "${ECS_CLUSTER}" \
  --service-name "${ECS_SERVICE_NAME}" \
  --query 'taskArns' \
  --output text 2>/dev/null || true)"

if [[ -n "${TASK_ARNS}" && "${TASK_ARNS}" != "None" ]]; then
  aws ecs describe-tasks \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --tasks ${TASK_ARNS} \
    --query 'tasks[].{taskArn:taskArn,lastStatus:lastStatus,desiredStatus:desiredStatus,healthStatus:healthStatus,stoppedReason:stoppedReason,containers:containers[].{name:name,lastStatus:lastStatus,exitCode:exitCode,reason:reason}}' \
    --output json
else
  echo "No tasks found."
fi

echo
echo "== ALB URL =="
ALB_ARN="$(aws elbv2 describe-load-balancers \
  --region "${AWS_REGION}" \
  --names "${ALB_NAME}" \
  --query 'LoadBalancers[0].LoadBalancerArn' \
  --output text 2>/dev/null || true)"

if [[ -n "${ALB_ARN}" && "${ALB_ARN}" != "None" ]]; then
  ALB_DNS="$(aws elbv2 describe-load-balancers \
    --region "${AWS_REGION}" \
    --load-balancer-arns "${ALB_ARN}" \
    --query 'LoadBalancers[0].DNSName' \
    --output text)"
  echo "http://${ALB_DNS}"
else
  echo "ALB not found: ${ALB_NAME}"
fi

echo
echo "== Latest logs =="
aws logs tail "${ECS_LOG_GROUP}" \
  --region "${AWS_REGION}" \
  --since 30m || true
