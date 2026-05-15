#!/usr/bin/env bash
set -euo pipefail

compose_files=(-f deploy/docker-compose.yml -f deploy/docker-compose.ci.yml)
rendered_file="$(mktemp)"
trap 'rm -f "$rendered_file"' EXIT

docker compose "${compose_files[@]}" config > "$rendered_file"

required_services=(
  postgres
  redis
  rabbitmq
  command-center-bootstrapper
  command-center-gateway
  command-center-web
  command-center-operations-api
  command-center-discovery-api
  command-center-worker-control-api
  command-center-maintenance-api
  command-center-updates-api
  command-center-realtime
  gatekeeper
  worker-spider
  worker-http-requester
  worker-enum
  worker-portscan
  worker-highvalue
  worker-techid
)

service_list="$(docker compose "${compose_files[@]}" config --services)"

for service in "${required_services[@]}"; do
  if ! grep -qx "$service" <<<"$service_list"; then
    echo "Required Compose service is missing: $service" >&2
    exit 1
  fi
done

required_healthchecked_services=(
  command-center-gateway
  command-center-web
  command-center-operations-api
  command-center-discovery-api
  command-center-worker-control-api
  command-center-maintenance-api
  command-center-updates-api
  command-center-realtime
)

for service in "${required_healthchecked_services[@]}"; do
  if ! awk -v service="$service" '
      $0 ~ "^  " service ":" { in_service=1; next }
      in_service && /^  [A-Za-z0-9_.-]+:/ { in_service=0 }
      in_service && /healthcheck:/ { found_health=1 }
      in_service && /\/health\/ready/ { found_ready=1 }
      END { exit(found_health && found_ready ? 0 : 1) }
    ' "$rendered_file"; then
    echo "Service '$service' must define a /health/ready healthcheck." >&2
    exit 1
  fi
done

if ! grep -q 'ASPNETCORE_ENVIRONMENT: Production' "$rendered_file"; then
  echo "CI Compose overlay must run application services in Production." >&2
  exit 1
fi

if grep -q 'local-dev-diagnostics-key-change-me' "$rendered_file"; then
  echo "Rendered CI Compose config still contains the local development diagnostics key." >&2
  exit 1
fi

echo "Deployment manifests validated."
