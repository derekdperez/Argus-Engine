#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

: "${AWS_REGION:?Set AWS_REGION}"
: "${ECS_CLUSTER:?Set ECS_CLUSTER}"

sampled_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"

services=(
  "${WORKER_SPIDER_SERVICE:-argus-worker-spider}"
  "${WORKER_ENUM_SERVICE:-argus-worker-enum}"
  "${WORKER_PORTSCAN_SERVICE:-argus-worker-portscan}"
  "${WORKER_HIGHVALUE_SERVICE:-argus-worker-highvalue}"
  "${WORKER_TECHID_SERVICE:-argus-worker-techid}"
)

docker_cmd() {
  if docker info >/dev/null 2>&1; then
    docker "$@"
  else
    sudo docker "$@"
  fi
}

compose_cmd() {
  if docker_cmd compose version >/dev/null 2>&1; then
    docker_cmd compose -f "${repo_root}/deploy/docker-compose.yml" "$@"
  elif command -v docker-compose >/dev/null 2>&1; then
    if docker info >/dev/null 2>&1; then
      docker-compose -f "${repo_root}/deploy/docker-compose.yml" "$@"
    else
      sudo docker-compose -f "${repo_root}/deploy/docker-compose.yml" "$@"
    fi
  else
    echo "Docker Compose is not available." >&2
    exit 1
  fi
}

metadata_token() {
  curl -fsS -X PUT "http://169.254.169.254/latest/api/token" \
    -H "X-aws-ec2-metadata-token-ttl-seconds: 21600" 2>/dev/null || true
}

metadata_get() {
  local path="$1"
  if [[ -n "${IMDS_TOKEN:-}" ]]; then
    curl -fsS -H "X-aws-ec2-metadata-token: ${IMDS_TOKEN}" "http://169.254.169.254/latest/${path}" 2>/dev/null || true
  else
    curl -fsS "http://169.254.169.254/latest/${path}" 2>/dev/null || true
  fi
}

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT

ecs_json="${tmpdir}/ecs-services.json"
csv_file="${tmpdir}/usage.csv"

aws ecs describe-services \
  --region "$AWS_REGION" \
  --cluster "$ECS_CLUSTER" \
  --services "${services[@]}" \
  --output json >"$ecs_json"

IMDS_TOKEN="$(metadata_token)"
identity_json="$(metadata_get dynamic/instance-identity/document)"

SAMPLED_AT="$sampled_at" \
IDENTITY_JSON="$identity_json" \
python3 - "$ecs_json" >"$csv_file" <<'PY'
import csv
import json
import os
import sys

sampled_at = os.environ["SAMPLED_AT"]
with open(sys.argv[1], encoding="utf-8") as handle:
    ecs = json.load(handle)

writer = csv.writer(sys.stdout, lineterminator="\n")
for service in ecs.get("services", []):
    name = service.get("serviceName", "")
    if not name:
        continue
    metadata = {
        "desiredCount": service.get("desiredCount", 0),
        "pendingCount": service.get("pendingCount", 0),
        "taskDefinition": service.get("taskDefinition", ""),
        "status": service.get("status", ""),
    }
    writer.writerow([
        sampled_at,
        "ecs-worker",
        name,
        name,
        int(service.get("runningCount", 0)),
        json.dumps(metadata, separators=(",", ":")),
    ])

identity_raw = os.environ.get("IDENTITY_JSON", "").strip()
if identity_raw:
    try:
        identity = json.loads(identity_raw)
    except json.JSONDecodeError:
        identity = {}
    instance_id = identity.get("instanceId", "")
    if instance_id:
        metadata = {
            "region": identity.get("region", ""),
            "availabilityZone": identity.get("availabilityZone", ""),
            "launchTime": identity.get("pendingTime", ""),
        }
        writer.writerow([
            sampled_at,
            "ec2-server",
            instance_id,
            "command-center-ec2",
            1,
            json.dumps(metadata, separators=(",", ":")),
        ])
PY

compose_cmd exec -T postgres psql -U argus -d "${ARGUS_DB_NAME:-argus_engine}" -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
CREATE TABLE IF NOT EXISTS cloud_resource_usage_samples (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sampled_at_utc timestamp with time zone NOT NULL,
    resource_kind character varying(64) NOT NULL,
    resource_id character varying(256) NOT NULL,
    resource_name character varying(256) NOT NULL,
    running_count integer NOT NULL,
    metadata_json jsonb NULL
);

CREATE INDEX IF NOT EXISTS ix_cloud_resource_usage_kind_resource_sampled
    ON cloud_resource_usage_samples (resource_kind, resource_id, sampled_at_utc);
SQL

compose_cmd exec -T postgres psql -U argus -d "${ARGUS_DB_NAME:-argus_engine}" \
  -c "\copy cloud_resource_usage_samples (sampled_at_utc, resource_kind, resource_id, resource_name, running_count, metadata_json) FROM STDIN WITH (FORMAT csv)" \
  <"$csv_file" >/dev/null

echo "Recorded cloud usage sample at ${sampled_at}"
