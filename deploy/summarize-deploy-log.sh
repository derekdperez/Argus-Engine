#!/usr/bin/env bash
# Summarize Argus deployment logs and surface the first actionable failure.
#
# Usage:
#   ./deploy/summarize-deploy-log.sh [deploy/logs/deploy_summary_YYYY-MM-DD_HH-MM-SS.log]
#
# With no argument, the newest deploy/logs/deploy_summary_*.log file is used.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_FILE="${1:-}"

if [[ -z "$LOG_FILE" ]]; then
  LOG_FILE="$(ls -t "$ROOT"/deploy/logs/deploy_summary_*.log 2>/dev/null | head -n 1 || true)"
fi

if [[ -z "$LOG_FILE" || ! -f "$LOG_FILE" ]]; then
  echo "ERROR: deploy log not found." >&2
  echo "Provide a log path or run a deployment that writes deploy/logs/deploy_summary_*.log." >&2
  exit 1
fi

print_section() {
  printf '\n== %s ==\n' "$1"
}

first_timestamp="$(grep -m1 -oE '\[[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:.]{12}\]' "$LOG_FILE" | tr -d '[]' || true)"
last_timestamp="$(grep -oE '\[[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:.]{12}\]' "$LOG_FILE" | tail -n 1 | tr -d '[]' || true)"

print_section "Deployment log"
echo "file: $LOG_FILE"
[[ -z "$first_timestamp" ]] || echo "first timestamp: $first_timestamp"
[[ -z "$last_timestamp" ]] || echo "last timestamp:  $last_timestamp"

print_section "Deployment decision"
if grep -m1 -E 'Fast deploy: rebuilding changed/stale service image\(s\):|Fast deploy: no service image rebuild required|Fresh deploy:' "$LOG_FILE"; then
  :
else
  echo "No incremental/fresh deploy decision line found."
fi

print_section "Services selected for image rebuild"
selected_line="$(grep -m1 -E 'Fast deploy: rebuilding changed/stale service image\(s\):' "$LOG_FILE" || true)"
if [[ -n "$selected_line" ]]; then
  printf '%s\n' "$selected_line" \
    | sed -E 's/^.*changed\/stale service image\(s\):[[:space:]]*//' \
    | tr ' ' '\n' \
    | sed '/^$/d' \
    | sed 's/^/- /'
else
  echo "No changed/stale image rebuild list found."
fi

print_section "Compiler and Docker build failures"
failure_pattern='(: error CS[0-9]+:|target .*: failed to solve|did not complete successfully: exit code:|^ERROR:|[[:space:]]ERROR:)'
if grep -nE "$failure_pattern" "$LOG_FILE"; then
  :
else
  echo "No compiler/Docker failure lines matched."
fi

first_failure_line="$(grep -nE "$failure_pattern" "$LOG_FILE" | head -n 1 | cut -d: -f1 || true)"
if [[ -n "$first_failure_line" ]]; then
  start=$(( first_failure_line > 25 ? first_failure_line - 25 : 1 ))
  end=$(( first_failure_line + 25 ))
  print_section "First failure context"
  sed -n "${start},${end}p" "$LOG_FILE"
fi

print_section "Slowest visible Docker build steps"
# Docker BuildKit plain progress emits lines like:
#   #98 51.47 /src/... error ...
# The second column is seconds elapsed within that build step.
grep -E '^#[0-9]+[[:space:]]+[0-9]+(\.[0-9]+)?[[:space:]]' "$LOG_FILE" \
  | awk '{ step=$1; seconds=$2; $1=""; $2=""; sub(/^  */, "", $0); print seconds "\t" step "\t" $0 }' \
  | sort -nr \
  | head -n 20 \
  | awk -F '\t' '{ printf "%7ss  %-6s %s\n", $1, $2, $3 }' \
  || true

print_section "Next command hints"
cat <<'HINTS'
# Show the newest deploy log summary:
./deploy/summarize-deploy-log.sh

# Re-run with readable BuildKit output:
argus_BUILD_PROGRESS=plain ./deploy/deploy.sh

# If the first error is a compile error, build that project locally before rebuilding every image:
dotnet build src/ArgusEngine.CommandCenter.Web/ArgusEngine.CommandCenter.Web.csproj
HINTS
