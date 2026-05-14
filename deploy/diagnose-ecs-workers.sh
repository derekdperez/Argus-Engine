#!/usr/bin/env bash
set -euo pipefail

# Diagnoses the "ECS tasks are running but not doing work" failure mode.
#
# Run on the deployment host from the repository root:
#   set -a
#   . deploy/aws/.env.generated
#   set +a
#   ./deploy/diagnose-ecs-workers.sh

COMPOSE_FILE="${COMPOSE_FILE:-deploy/docker-compose.yml}"
POSTGRES_SERVICE="${POSTGRES_SERVICE:-postgres}"
POSTGRES_USER="${POSTGRES_USER:-argus}"
POSTGRES_DB="${POSTGRES_DB:-argus_engine}"
AWS_REGION="${AWS_REGION:-us-east-1}"
ECS_CLUSTER="${ECS_CLUSTER:-argus-engine}"

services=(
  "${WORKER_SPIDER_SERVICE:-argus-worker-spider}"
  "${WORKER_ENUM_SERVICE:-argus-worker-enum}"
  "${WORKER_PORTSCAN_SERVICE:-argus-worker-portscan}"
  "${WORKER_HIGHVALUE_SERVICE:-argus-worker-highvalue}"
  "${WORKER_TECHID_SERVICE:-argus-worker-techid}"
)

echo "== Local DB worker heartbeat view =="
sudo docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_SERVICE" \
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
SELECT
    "WorkerKey",
    "HostName",
    "IsHealthy",
    "ActiveConsumerCount",
    "LastHeartbeatUtc",
    now() - "LastHeartbeatUtc" AS heartbeat_age,
    "HealthMessage"
FROM worker_heartbeats
ORDER BY "LastHeartbeatUtc" DESC;
SQL

echo
echo "== Queue backlog by state =="
sudo docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_SERVICE" \
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
SELECT
    state,
    count(*) AS rows
FROM http_request_queue
GROUP BY state
ORDER BY rows DESC;
SQL

echo
echo "== Recent consumer activity =="
sudo docker compose -f "$COMPOSE_FILE" exec -T "$POSTGRES_SERVICE" \
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
SELECT
    direction,
    consumer_type,
    host_name,
    max(occurred_at_utc) AS last_seen,
    count(*) AS events
FROM bus_journal
WHERE occurred_at_utc >= now() - interval '30 minutes'
GROUP BY direction, consumer_type, host_name
ORDER BY last_seen DESC
LIMIT 50;
SQL

echo
echo "== ECS service state =="
aws ecs describe-services \
  --region "$AWS_REGION" \
  --cluster "$ECS_CLUSTER" \
  --services "${services[@]}" \
  --query 'services[].{
    service:serviceName,
    status:status,
    desired:desiredCount,
    running:runningCount,
    pending:pendingCount,
    taskDefinition:taskDefinition,
    latestEvent:events[0].message
  }' \
  --output table

echo
echo "== ECS task network/config summary =="
for svc in "${services[@]}"; do
  echo
  echo "-- ${svc} --"
  task_arn="$(
    aws ecs list-tasks \
      --region "$AWS_REGION" \
      --cluster "$ECS_CLUSTER" \
      --service-name "$svc" \
      --desired-status RUNNING \
      --query 'taskArns[0]' \
      --output text
  )"

  if [[ -z "$task_arn" || "$task_arn" == "None" ]]; then
    echo "No running task."
    continue
  fi

  aws ecs describe-tasks \
    --region "$AWS_REGION" \
    --cluster "$ECS_CLUSTER" \
    --tasks "$task_arn" \
    --query 'tasks[0].{
      task:taskArn,
      lastStatus:lastStatus,
      desiredStatus:desiredStatus,
      startedAt:startedAt,
      connectivity:connectivity,
      stoppedReason:stoppedReason,
      containers:containers[].{
        name:name,
        lastStatus:lastStatus,
        healthStatus:healthStatus,
        exitCode:exitCode,
        reason:reason
      }
    }' \
    --output json
done

echo
echo "== ECS container logs, latest 50 lines per worker service =="
for svc in "${services[@]}"; do
  task_def="$(
    aws ecs describe-services \
      --region "$AWS_REGION" \
      --cluster "$ECS_CLUSTER" \
      --services "$svc" \
      --query 'services[0].taskDefinition' \
      --output text
  )"

  log_group="$(
    aws ecs describe-task-definition \
      --region "$AWS_REGION" \
      --task-definition "$task_def" \
      --query 'taskDefinition.containerDefinitions[0].logConfiguration.options."awslogs-group"' \
      --output text 2>/dev/null || true
  )"
