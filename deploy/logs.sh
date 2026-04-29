#!/usr/bin/env bash
# Show focused Docker Compose status and logs for Nightmare v2.
#
# Usage:
#   ./deploy/logs.sh
#   ./deploy/logs.sh command-center worker-spider
#   ./deploy/logs.sh --follow worker-enum
#   TAIL=300 ./deploy/logs.sh --errors
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# shellcheck source=deploy/lib-nightmare-compose.sh
source "$DEPLOY_DIR/lib-nightmare-compose.sh"

TAIL="${TAIL:-160}"
FOLLOW=0
ERRORS_ONLY=0
SERVICES=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    -f | --follow)
      FOLLOW=1
      shift
      ;;
    --errors)
      ERRORS_ONLY=1
      shift
      ;;
    -n | --tail)
      TAIL="${2:-160}"
      shift 2
      ;;
    -h | --help)
      cat <<'EOF'
Usage: ./deploy/logs.sh [--follow] [--errors] [--tail N] [service...]

Examples:
  ./deploy/logs.sh
  ./deploy/logs.sh command-center worker-spider
  ./deploy/logs.sh --errors
  ./deploy/logs.sh --follow worker-enum
EOF
      exit 0
      ;;
    *)
      SERVICES+=("$1")
      shift
      ;;
  esac
done

printf '== Compose status ==\n'
compose ps || true
printf '\n'

if [[ "$ERRORS_ONLY" == "1" ]]; then
  printf '== Recent error-like log lines ==\n'
  compose logs --tail "$TAIL" "${SERVICES[@]}" \
    | grep -Ei '(^|[^a-z])(fail|failed|fatal|panic|exception|error|critical|unhandled| 404 |status=404|OptionsValidationException)([^a-z]|$)' \
    || true
  exit 0
fi

printf '== Logs tail=%s ==\n' "$TAIL"
if [[ "$FOLLOW" == "1" ]]; then
  compose logs --tail "$TAIL" -f "${SERVICES[@]}"
else
  compose logs --tail "$TAIL" "${SERVICES[@]}"
  printf '\n== Error-like highlights ==\n'
  compose logs --tail "$TAIL" "${SERVICES[@]}" \
    | grep -Ei '(^|[^a-z])(fail|failed|fatal|panic|exception|error|critical|unhandled| 404 |status=404|OptionsValidationException)([^a-z]|$)' \
    || true
fi
