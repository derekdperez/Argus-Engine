#!/usr/bin/env bash
# Codespaces-friendly wrapper for the Argus Engine local deployment script.
#
# This wrapper intentionally keeps the deployment implementation in ./deploy-local.sh
# and only adds GitHub Codespaces defaults plus forwarded-port URL reporting.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_LOCAL="$ROOT/deploy-local.sh"

die() {
  echo "ERROR: $*" >&2
  exit 1
}

is_codespace() {
  [[ "${CODESPACES:-}" == "true" ]]
}

configure_codespace_defaults() {
  is_codespace || return 0

  # Smoke checks should use the in-container/local listener, not the external
  # forwarded URL. The browser-facing URL is printed separately below.
  export ARGUS_LOCAL_BASE_URL="${ARGUS_LOCAL_BASE_URL:-http://127.0.0.1:8080}"

  # Keep the default Codespace footprint small. Developers can override these
  # with deploy-local.sh scale flags or ARGUS_LOCAL_SCALE_WORKER_* variables.
  export ARGUS_LOCAL_SCALE_WORKER_SPIDER="${ARGUS_LOCAL_SCALE_WORKER_SPIDER:-1}"
  export ARGUS_LOCAL_SCALE_WORKER_ENUM="${ARGUS_LOCAL_SCALE_WORKER_ENUM:-1}"
  export ARGUS_LOCAL_SCALE_WORKER_PORTSCAN="${ARGUS_LOCAL_SCALE_WORKER_PORTSCAN:-0}"
  export ARGUS_LOCAL_SCALE_WORKER_HIGHVALUE="${ARGUS_LOCAL_SCALE_WORKER_HIGHVALUE:-1}"
  export ARGUS_LOCAL_SCALE_WORKER_TECHID="${ARGUS_LOCAL_SCALE_WORKER_TECHID:-1}"
  export ARGUS_LOCAL_SCALE_WORKER_HTTP_REQUESTER="${ARGUS_LOCAL_SCALE_WORKER_HTTP_REQUESTER:-1}"

  # Compose build output is noisy in Codespaces and BuildKit is faster/more cacheable.
  export DOCKER_BUILDKIT="${DOCKER_BUILDKIT:-1}"
  export COMPOSE_BAKE="${COMPOSE_BAKE:-false}"
}

codespace_url_for_port() {
  local port="$1"

  if is_codespace && [[ -n "${CODESPACE_NAME:-}" ]]; then
    local domain="${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-app.github.dev}"
    printf 'https://%s-%s.%s\n' "$CODESPACE_NAME" "$port" "$domain"
  else
    printf 'http://localhost:%s\n' "$port"
  fi
}

command_for_args() {
  local arg
  for arg in "$@"; do
    case "$arg" in
      up|status|ps|logs|restart|down|clean|smoke)
        printf '%s\n' "$arg"
        return 0
        ;;
      -h|--help|help)
        printf 'help\n'
        return 0
        ;;
    esac
  done

  printf 'up\n'
}

is_start_command() {
  [[ "$(command_for_args "$@")" == "up" ]]
}

print_codespace_urls() {
  echo ""
  echo "GitHub Codespaces forwarded URLs:"
  echo "  Command Center: $(codespace_url_for_port 8080)"
  echo "  RabbitMQ UI:    $(codespace_url_for_port 15672)"
  echo ""
  echo "Port visibility is controlled from the GitHub Codespaces Ports tab."
  echo ""
  echo "Useful commands:"
  echo "  ./deploy/run-local-codespace.sh status"
  echo "  ./deploy/run-local-codespace.sh logs --follow command-center"
  echo "  ./deploy/run-local-codespace.sh down"
}

main() {
  [[ -f "$DEPLOY_LOCAL" ]] || die "Missing $DEPLOY_LOCAL. Run this script from an Argus-Engine checkout."

  local original_args=("$@")
  configure_codespace_defaults

  bash "$DEPLOY_LOCAL" "${original_args[@]}"
  local exit_code=$?

  if [[ "$exit_code" -eq 0 ]] && is_start_command "${original_args[@]}"; then
    print_codespace_urls
  fi

  return "$exit_code"
}

main "$@"
