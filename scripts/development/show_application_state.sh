#!/usr/bin/env bash
# Show the current Argus Engine local/EC2 development stack state.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/development/common.sh
. "$SCRIPT_DIR/common.sh"

TAIL_LINES=100
INCLUDE_DB=1
INCLUDE_RABBIT=1
INCLUDE_LOG_ERRORS=1

usage() {
  cat <<'EOF'
Usage:
  ./scripts/development/show_application_state.sh [options]

Options:
  --tail N        Recent log lines used for error summary. Default: 100.
  --no-db         Skip Postgres checks.
  --no-rabbit     Skip RabbitMQ queue checks.
  --no-logs       Skip recent error-like log summary.
  -h, --help      Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tail)
      [[ $# -ge 2 ]] || argus_dev_die "--tail requires a value"
      TAIL_LINES="$2"
      shift 2
      ;;
    --no-db)
      INCLUDE_DB=0
      shift
      ;;
    --no-rabbit)
      INCLUDE_RABBIT=0
      shift
      ;;
    --no-logs)
      INCLUDE_LOG_ERRORS=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      argus_dev_die "Unknown argument: $1"
      ;;
  esac
done

cd "$ARGUS_DEV_ROOT"

argus_dev_section "URLs"
argus_dev_print_urls

argus_dev_section "Git"
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "branch=$(git branch --show-current 2>/dev/null || true)"
  echo "commit=$(git rev-parse --short=12 HEAD 2>/dev/null || true)"
  if git diff --quiet HEAD -- 2>/dev/null; then
    echo "working_tree=clean"
  else
    echo "working_tree=dirty"
  fi
else
  echo "Not a Git working tree."
fi

argus_dev_section "Docker Compose"
argus_dev_compose ps || true

argus_dev_section "Container health"
while read -r service; do
  [[ -n "$service" ]] || continue
  cid="$(argus_dev_container_id "$service" || true)"
  if [[ -z "$cid" ]]; then
    printf '%-24s %s\n' "$service" "missing"
    continue
  fi
  argus_dev_docker inspect \
    --format '{{.Name}} status={{.State.Status}} health={{if .State.Health}}{{.State.Health.Status}}{{else}}n/a{{end}} started={{.State.StartedAt}}' \
    "$cid" 2>/dev/null || true
done < <(argus_dev_services)

argus_dev_section "Application API health"
for path in \
  /health/ready \
  /api/status/summary \
  /api/workers/health \
  /api/http-request-queue/metrics
do
  echo ""
  echo "GET $path"
  if ! argus_dev_curl_json "$path"; then
    argus_dev_warn "Failed to fetch $path"
  fi
done

if [[ "$INCLUDE_RABBIT" == "1" ]]; then
  argus_dev_section "RabbitMQ queues"
  rabbit_cid="$(argus_dev_container_id rabbitmq || true)"
  if [[ -n "$rabbit_cid" ]]; then
    argus_dev_docker exec "$rabbit_cid" rabbitmqctl list_queues name messages_ready messages_unacknowledged consumers 2>/dev/null || true
  else
    echo "RabbitMQ container not found."
  fi
fi

if [[ "$INCLUDE_DB" == "1" ]]; then
  argus_dev_section "Postgres database summary"
  pg_cid="$(argus_dev_container_id postgres || true)"
  if [[ -n "$pg_cid" ]]; then
    argus_dev_docker exec -e PGPASSWORD=argus "$pg_cid" \
      psql -U argus -d argus_engine -v ON_ERROR_STOP=0 -c "select current_database() as database, now() as checked_at;" 2>/dev/null || true

    argus_dev_docker exec -e PGPASSWORD=argus "$pg_cid" \
      psql -U argus -d argus_engine -v ON_ERROR_STOP=0 -c "select relname as table, n_live_tup as estimated_rows from pg_stat_user_tables order by n_live_tup desc, relname asc limit 25;" 2>/dev/null || true

    argus_dev_docker exec -e PGPASSWORD=argus "$pg_cid" \
      psql -U argus -d argus_engine -v ON_ERROR_STOP=0 -c "select state, count(*) from http_request_queue group by state order by state;" 2>/dev/null || true
  else
    echo "Postgres container not found."
  fi
fi

if [[ "$INCLUDE_LOG_ERRORS" == "1" ]]; then
  argus_dev_section "Recent error-like logs"
  argus_dev_compose logs "--tail=$TAIL_LINES" 2>&1 \
    | grep -Ei '(^|[^a-z])(fail(ed|ure)?|fatal|panic|exception|unhandled|critical|error|timeout|denied|refused|unhealthy)([^a-z]|$)' \
    || echo "No recent error-like log lines found."
fi
