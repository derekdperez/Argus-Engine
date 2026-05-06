#!/usr/bin/env bash
# Shared helpers for Argus Engine EC2/local development scripts.
# Source this file; do not execute it directly.

set -Eeuo pipefail
IFS=$'\n\t'

argus_dev_script_dir() {
  cd "$(dirname "${BASH_SOURCE[0]}")" && pwd
}

argus_dev_repo_root() {
  local dir
  dir="$(argus_dev_script_dir)"
  cd "$dir/../.." && pwd
}

ARGUS_DEV_ROOT="${ARGUS_DEV_ROOT:-$(argus_dev_repo_root)}"
ARGUS_DEV_DEPLOY_DIR="$ARGUS_DEV_ROOT/deploy"
ARGUS_DEV_COMPOSE_FILE="$ARGUS_DEV_DEPLOY_DIR/docker-compose.yml"
ARGUS_DEV_BASE_URL="${ARGUS_DEV_BASE_URL:-${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8080}}"

argus_dev_section() {
  echo ""
  echo "### $*"
}

argus_dev_warn() {
  echo "WARN: $*" >&2
}

argus_dev_die() {
  echo "ERROR: $*" >&2
  exit 1
}

argus_dev_load_env() {
  local file
  for file in "$ARGUS_DEV_ROOT/.env.local" "$ARGUS_DEV_DEPLOY_DIR/.env.local" "$ARGUS_DEV_ROOT/.env"; do
    if [[ -f "$file" ]]; then
      set -a
      # shellcheck disable=SC1090
      . "$file"
      set +a
    fi
  done
}

argus_dev_docker() {
  if docker info >/dev/null 2>&1; then
    docker "$@"
  elif command -v sudo >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
    sudo docker "$@"
  else
    argus_dev_die "Docker is unavailable or inaccessible to the current user."
  fi
}

argus_dev_compose() {
  [[ -f "$ARGUS_DEV_COMPOSE_FILE" ]] || argus_dev_die "Missing $ARGUS_DEV_COMPOSE_FILE"

  if argus_dev_docker compose version >/dev/null 2>&1; then
    argus_dev_docker compose -f "$ARGUS_DEV_COMPOSE_FILE" "$@"
  elif command -v docker-compose >/dev/null 2>&1; then
    docker-compose -f "$ARGUS_DEV_COMPOSE_FILE" "$@"
  else
    argus_dev_die "Docker Compose is not installed."
  fi
}

argus_dev_services() {
  cat <<'EOF'
postgres
filestore-db-init
redis
rabbitmq
command-center
gatekeeper
worker-spider
worker-enum
worker-portscan
worker-highvalue
worker-techid
EOF
}

argus_dev_app_services() {
  cat <<'EOF'
command-center
gatekeeper
worker-spider
worker-enum
worker-portscan
worker-highvalue
worker-techid
EOF
}

argus_dev_curl_json() {
  local path="$1"
  local url="$ARGUS_DEV_BASE_URL$path"

  if command -v jq >/dev/null 2>&1; then
    curl -fsS --max-time 15 "$url" | jq .
  else
    curl -fsS --max-time 15 "$url"
    echo
  fi
}

argus_dev_container_id() {
  local service="$1"
  argus_dev_compose ps -q "$service" | tail -n 1
}

argus_dev_public_host() {
  if [[ -n "${ARGUS_LOCAL_PUBLIC_HOST:-}" ]]; then
    printf '%s\n' "$ARGUS_LOCAL_PUBLIC_HOST"
    return
  fi

  if command -v curl >/dev/null 2>&1; then
    local token public_ipv4
    token="$(curl -fsS --max-time 1 -X PUT \
      -H 'X-aws-ec2-metadata-token-ttl-seconds: 60' \
      http://169.254.169.254/latest/api/token 2>/dev/null || true)"
    if [[ -n "$token" ]]; then
      public_ipv4="$(curl -fsS --max-time 1 \
        -H "X-aws-ec2-metadata-token: $token" \
        http://169.254.169.254/latest/meta-data/public-ipv4 2>/dev/null || true)"
      if [[ -n "$public_ipv4" ]]; then
        printf '%s\n' "$public_ipv4"
        return
      fi
    fi
  fi

  printf 'localhost\n'
}

argus_dev_print_urls() {
  local host
  host="$(argus_dev_public_host)"
  echo "Command Center: http://localhost:8080/"
  if [[ "$host" != "localhost" ]]; then
    echo "EC2/public URL: http://$host:8080/"
  fi
  echo "RabbitMQ UI:    http://localhost:15672/ user/pass: argus / argus"
  echo "Postgres:       localhost:5432 db=argus_engine user=argus password=argus"
  echo "Redis:          localhost:6379"
}

argus_dev_load_env
