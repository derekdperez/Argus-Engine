#!/usr/bin/env bash
# Show useful logs from an EC2/local development machine and the Argus containers.

set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/development/common.sh
. "$SCRIPT_DIR/common.sh"

TAIL_LINES=250
FOLLOW=0
ERRORS_ONLY=0
INCLUDE_MACHINE=1
SERVICES=()

usage() {
  cat <<'EOF'
Usage:
  ./scripts/development/show_development_machine_logs.sh [options] [service...]

Options:
  --tail N       Log lines to show. Default: 250.
  --follow, -f   Follow Compose logs.
  --errors       Show error-like application log lines only.
  --no-machine   Do not include EC2/Docker host logs.
  -h, --help     Show this help.

Examples:
  ./scripts/development/show_development_machine_logs.sh
  ./scripts/development/show_development_machine_logs.sh --errors
  ./scripts/development/show_development_machine_logs.sh --follow worker-spider
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tail)
      [[ $# -ge 2 ]] || argus_dev_die "--tail requires a value"
      TAIL_LINES="$2"
      shift 2
      ;;
    --follow|-f)
      FOLLOW=1
      shift
      ;;
    --errors)
      ERRORS_ONLY=1
      shift
      ;;
    --no-machine)
      INCLUDE_MACHINE=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      SERVICES+=("$1")
      shift
      ;;
  esac
done

cd "$ARGUS_DEV_ROOT"

if [[ "$INCLUDE_MACHINE" == "1" ]]; then
  argus_dev_section "Machine"
  {
    echo "time_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "hostname=$(hostname)"
    echo "kernel=$(uname -a)"
    echo "uptime=$(uptime || true)"
  } || true

  argus_dev_section "Disk"
  df -h || true

  argus_dev_section "Memory"
  free -h 2>/dev/null || vm_stat 2>/dev/null || true

  argus_dev_section "Docker daemon journal"
  if command -v journalctl >/dev/null 2>&1; then
    sudo journalctl -u docker --no-pager -n "$TAIL_LINES" 2>/dev/null || journalctl -u docker --no-pager -n "$TAIL_LINES" 2>/dev/null || true
  else
    echo "journalctl not available."
  fi

  argus_dev_section "Cloud-init/user-data logs"
  if [[ -r /var/log/cloud-init-output.log ]]; then
    tail -n "$TAIL_LINES" /var/log/cloud-init-output.log || true
  else
    sudo tail -n "$TAIL_LINES" /var/log/cloud-init-output.log 2>/dev/null || true
  fi
fi

argus_dev_section "Compose status"
argus_dev_compose ps || true

argus_dev_section "Application logs"
log_args=(logs "--tail=$TAIL_LINES")
[[ "$FOLLOW" == "1" ]] && log_args+=(-f)
if [[ ${#SERVICES[@]} -gt 0 ]]; then
  log_args+=("${SERVICES[@]}")
fi

if [[ "$ERRORS_ONLY" == "1" ]]; then
  argus_dev_compose "${log_args[@]}" 2>&1 \
    | grep -Ei --line-buffered '(^|[^a-z])(fail(ed|ure)?|fatal|panic|exception|unhandled|critical|error|timeout|denied|refused|unhealthy)([^a-z]|$)' \
    || true
else
  argus_dev_compose "${log_args[@]}"
fi
