#!/usr/bin/env bash
# Quick Deploy: update only the running Command Center Web App container.
#
# This path intentionally avoids the normal incremental deploy pipeline:
#   - no docker compose up
#   - no image rebuild
#   - no worker/API/container reconciliation
#   - only publish command-center-web, copy /app, and restart command-center-web
#
# Use a normal deploy for package changes, Dockerfile changes, compose changes,
# shared service/API changes, DB/schema changes, or first-time startup.

set -euo pipefail

export COMPOSE_PARALLEL_LIMIT="${COMPOSE_PARALLEL_LIMIT:-10}"

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

SERVICE="command-center-web"
WEB_BASE_URL="${ARGUS_WEB_BASE_URL:-http://127.0.0.1:8082}"

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"

if [[ -f "$DEPLOY_DIR/lib-fast-hot-swap.sh" ]]; then
  # shellcheck source=deploy/lib-fast-hot-swap.sh
  source "$DEPLOY_DIR/lib-fast-hot-swap.sh"
fi

argus_quick_read_file_value() {
  local file="$1"
  [[ -f "$file" ]] || return 0
  cat "$file"
}

argus_quick_update_fingerprint_entry() {
  local service="$1"
  local current_file="$2"
  local last_file="$3"
  local current tmp normalized service_name value

  [[ -f "$current_file" ]] || return 0
  current="$(argus_read_fingerprint "$service" "$current_file")"
  [[ -n "$current" ]] || return 0

  mkdir -p "$(dirname "$last_file")"
  tmp="${last_file}.quick.tmp"
  normalized="${last_file}.quick.normalized"

  if [[ -f "$last_file" ]]; then
    awk -v svc="$service" '$1 != svc { print }' "$last_file" >"$tmp"
  else
    : >"$tmp"
  fi

  printf '%s %s\n' "$service" "$current" >>"$tmp"

  : >"$normalized"
  while IFS= read -r service_name; do
    value="$(argus_read_fingerprint "$service_name" "$tmp")"
    [[ -n "$value" ]] && printf '%s %s\n' "$service_name" "$value" >>"$normalized"
  done < <(argus_all_dotnet_services)

  mv -f "$normalized" "$last_file"
  rm -f "$tmp"
}

argus_quick_require_running_web_container() {
  local cid running
  cid="$(compose ps -q "$SERVICE" | tail -n 1 || true)"
  if [[ -z "$cid" ]]; then
    echo "ERROR: $SERVICE has no container. Run a normal deploy first." >&2
    exit 1
  fi

  running="$(argus_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
  if [[ "$running" != "true" ]]; then
    echo "ERROR: $SERVICE exists but is not running. Run a normal deploy or restart it first." >&2
    compose ps "$SERVICE" >&2 || true
    exit 1
  fi
}

argus_quick_guard_against_non_web_deploy_inputs() {
  local current_image last_image current_runtime last_runtime source_now source_last

  argus_compute_current_fingerprints "$ROOT"

  current_image="$(argus_read_fingerprint "$SERVICE" "$(argus_current_image_fingerprint_path)")"
  last_image="$(argus_read_fingerprint "$SERVICE" "$(argus_image_fingerprint_path)")"

  if [[ -n "$last_image" && -n "$current_image" && "$current_image" != "$last_image" && "${ARGUS_QUICK_DEPLOY_ALLOW_IMAGE_INPUT_CHANGE:-0}" != "1" ]]; then
    echo "ERROR: $SERVICE image inputs changed. Quick Deploy refuses Dockerfile/image-recipe changes." >&2
    echo "Run: ./deploy/deploy.sh --image up" >&2
    echo "Or set ARGUS_QUICK_DEPLOY_ALLOW_IMAGE_INPUT_CHANGE=1 only if you understand the risk." >&2
    exit 1
  fi

  current_runtime="$(argus_quick_read_file_value "$(argus_current_runtime_fingerprint_path)")"
  last_runtime="$(argus_quick_read_file_value "$(argus_runtime_fingerprint_path)")"

  if [[ -n "$last_runtime" && -n "$current_runtime" && "$current_runtime" != "$last_runtime" && "${ARGUS_QUICK_DEPLOY_ALLOW_RUNTIME_CONFIG_CHANGE:-0}" != "1" ]]; then
    echo "ERROR: compose/runtime configuration changed. Quick Deploy only updates web app code." >&2
    echo "Run: ./deploy/deploy.sh up" >&2
    echo "Or set ARGUS_QUICK_DEPLOY_ALLOW_RUNTIME_CONFIG_CHANGE=1 only if the runtime change is unrelated." >&2
    exit 1
  fi

  source_now="$(argus_read_fingerprint "$SERVICE" "$(argus_current_source_fingerprint_path)")"
  source_last="$(argus_read_fingerprint "$SERVICE" "$(argus_source_fingerprint_path)")"
  if [[ -n "$source_last" && -n "$source_now" && "$source_now" == "$source_last" && "${ARGUS_QUICK_DEPLOY_FORCE:-0}" != "1" ]]; then
    echo "No recorded $SERVICE source fingerprint change, but Quick Deploy will still republish current web output."
    echo "Set ARGUS_QUICK_DEPLOY_FORCE=1 to silence this message."
  fi
}

argus_quick_wait_for_web_asset() {
  local attempt tmp
  tmp="$(mktemp)"
  for attempt in {1..30}; do
    if curl -fsS --max-time 5 "$WEB_BASE_URL/_framework/blazor.web.js" -o "$tmp" >/dev/null 2>&1 && [[ -s "$tmp" ]] && ! grep -qi '^404: Not Found' "$tmp"; then
      rm -f "$tmp"
      return 0
    fi
    sleep 1
  done

  rm -f "$tmp"
  echo "ERROR: $SERVICE restarted, but $WEB_BASE_URL/_framework/blazor.web.js was not served successfully." >&2
  compose logs --tail=120 "$SERVICE" >&2 || true
  exit 1
}

echo ""
echo "Quick Deploy: Deploy Web App Only"
echo "Root: $ROOT"
echo "Service: $SERVICE"
echo ""

argus_quick_require_running_web_container
argus_quick_guard_against_non_web_deploy_inputs

export argus_HOT_SWAP_SERVICES="$SERVICE"
export argus_IMAGE_REBUILD_SERVICES=""

if declare -F argus_fast_hot_swap >/dev/null 2>&1; then
  argus_fast_hot_swap "$SERVICE"
else
  argus_hot_swap_services "$SERVICE"
fi

if [[ " ${argus_IMAGE_REBUILD_SERVICES:-} " == *" $SERVICE "* || " ${argus_HOT_SWAP_FALLBACK_SERVICES:-} " == *" $SERVICE "* ]]; then
  echo "ERROR: Quick Deploy publish/copy did not complete cleanly for $SERVICE." >&2
  echo "Quick Deploy will not rebuild images. Run a normal deploy instead:" >&2
  echo "  ./deploy/deploy.sh --image up" >&2
  exit 1
fi

argus_quick_update_fingerprint_entry "$SERVICE" "$(argus_current_fingerprint_path)" "$(argus_fingerprint_path)"
argus_quick_update_fingerprint_entry "$SERVICE" "$(argus_current_source_fingerprint_path)" "$(argus_source_fingerprint_path)"

# No Docker image was built. Preserve the image-input fingerprint for this service so the next
# normal deploy does not mistake a pure web hot-swap for an image-recipe change.
argus_quick_update_fingerprint_entry "$SERVICE" "$(argus_current_image_fingerprint_path)" "$(argus_image_fingerprint_path)"

argus_quick_wait_for_web_asset

echo ""
echo "Quick Deploy complete."
echo "Updated only: $SERVICE"
echo "Web: $WEB_BASE_URL/"
echo ""
