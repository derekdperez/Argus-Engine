#!/usr/bin/env bash
# Show focused Docker Compose status and logs for Argus Engine.
#
# Usage:
#   ./deploy/logs.sh
#   ./deploy/logs.sh command-center worker-spider
#   ./deploy/logs.sh --follow worker-enum
#   TAIL=300 ./deploy/logs.sh --errors
#   ERROR_CONTEXT=25 ./deploy/logs.sh --errors command-center-web
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"

TAIL="${TAIL:-160}"
FOLLOW=0
ERRORS_ONLY=0
SERVICES=()

# Keep a minimum of 15 lines of context around errors. Callers may increase this
# with ERROR_CONTEXT=25, but values lower than 15 are promoted to 15.
ERROR_CONTEXT="${ERROR_CONTEXT:-15}"
if ! [[ "$ERROR_CONTEXT" =~ ^[0-9]+$ ]]; then
  ERROR_CONTEXT=15
fi
if (( ERROR_CONTEXT < 15 )); then
  ERROR_CONTEXT=15
fi

# Match true error starts and exception summaries, but avoid treating generic
# stack-trace separators such as "--- End of inner exception stack trace ---"
# as new errors. API 404s are only highlighted when they are API calls, because
# ordinary static-asset/page 404s are usually noise.
ERROR_PATTERN='(^|[^a-z])(fail|failed|fatal|panic|error|critical|unhandled|an unhandled exception|exception was thrown|[a-z0-9_.]+exception:|optionsvalidationexception|deadlock detected|broker unreachable|missed heartbeats|connection refused|connection reset|relation "[^"]+" does not exist|/api/[^ ]* - 404|/api/[^ ]* status=404|status code 500| 500 )([^a-z]|$)'

usage() {
  cat <<'EOF'
Usage: ./deploy/logs.sh [--follow] [--errors] [--tail N] [service...]

Examples:
  ./deploy/logs.sh
  ./deploy/logs.sh command-center worker-spider
  ./deploy/logs.sh --errors
  ./deploy/logs.sh --errors command-center-web
  ERROR_CONTEXT=25 ./deploy/logs.sh --errors
  ./deploy/logs.sh --follow worker-enum

Options:
  --errors        Show error-like log lines with at least 15 lines of context
                  before and after each match.
  --tail N        Number of recent log lines to inspect per selected service.
  --follow        Follow logs.
EOF
}

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
      usage
      exit 0
      ;;
    *)
      SERVICES+=("$1")
      shift
      ;;
  esac
done

print_error_context() {
  local tmp
  tmp="$(mktemp "${TMPDIR:-/tmp}/argus-logs-errors.XXXXXX")"

  # Capture stderr too, because "no such service" and Docker/Compose failures are
  # diagnostic context the caller needs.
  compose logs --tail "$TAIL" "${SERVICES[@]}" >"$tmp" 2>&1 || true

  awk -v ctx="$ERROR_CONTEXT" -v pat="$ERROR_PATTERN" '
    {
      lines[NR] = $0
      lower = tolower($0)

      if (lower ~ pat) {
        start = NR - ctx
        if (start < 1) {
          start = 1
        }

        finish = NR + ctx
        for (i = start; i <= finish; i++) {
          mark[i] = 1
        }

        matched[NR] = 1
      }
    }

    END {
      printed = 0
      in_block = 0
      block_no = 0

      for (i = 1; i <= NR; i++) {
        if (mark[i]) {
          if (!in_block) {
            if (printed) {
              print ""
            }

            block_no++
            printf "---- error context block %d (±%d lines) ----\n", block_no, ctx
            in_block = 1
          }

          prefix = matched[i] ? ">> " : "   "
          printf "%s%s\n", prefix, lines[i]
          printed = 1
        } else {
          in_block = 0
        }
      }

      if (!printed) {
        print "(no recent error-like log lines found)"
      }
    }
  ' "$tmp"

  rm -f "$tmp"
}

printf '== Compose status ==\n'
compose ps || true
printf '\n'

if [[ "$ERRORS_ONLY" == "1" ]]; then
  printf '== Recent error-like log context, tail=%s, context=±%s ==\n' "$TAIL" "$ERROR_CONTEXT"
  print_error_context
  exit 0
fi

printf '== Logs tail=%s ==\n' "$TAIL"
if [[ "$FOLLOW" == "1" ]]; then
  compose logs --tail "$TAIL" -f "${SERVICES[@]}"
else
  compose logs --tail "$TAIL" "${SERVICES[@]}"
  printf '\n== Error-like highlights with context ==\n'
  print_error_context
fi
