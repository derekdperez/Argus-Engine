#!/usr/bin/env bash
set -euo pipefail

# AI Agent Coordination Script
# Usage: bash .ai-coordination/scripts/coordinate.sh <action> <agent-name> [args...]
#
# Actions:
#   claim    <agent> <file-path> <purpose>   - Claim a file for editing
#   release  <agent> <file-path>             - Release a claimed file
#   push     <agent> <commit-message>        - Stage, pull, commit, push all changes
#   heartbeat <agent>                        - Update last_active timestamp
#   share    <agent> <file-path>             - Mark a file as shared between agents
#   note     <agent> <message>               - Leave a note for other agents
#   status                                    - Show current coordination status

STATUS_FILE=".ai-coordination/status.json"
AGENT_NAME="${2:-}"
ACTION="${1:-}"

if [ -z "$ACTION" ]; then
  echo "Usage: $0 <action> <agent-name> [args...]"
  echo ""
  echo "Actions:"
  echo "  claim    <agent> <file-path> <purpose>"
  echo "  release  <agent> <file-path>"
  echo "  push     <agent> <commit-message>"
  echo "  heartbeat <agent>"
  echo "  share    <agent> <file-path>"
  echo "  note     <agent> <message>"
  echo "  status"
  exit 1
fi

# ---------- Utility Functions ----------

pull_latest() {
  echo "[coordinate] Pulling latest changes from remote..."
  git pull --rebase 2>/dev/null || {
    echo "[coordinate] Pull failed. Stashing local changes and retrying..."
    git stash
    git pull --rebase
    git stash pop
  }
}

push_changes() {
  local agent="$1"
  local message="$2"

  echo "[coordinate] Staging all changes..."
  git add -A

  # Check if there's anything to commit
  if git diff --cached --quiet; then
    echo "[coordinate] No changes to commit."
    return 0
  fi

  # Pull latest before push to catch conflicts early
  echo "[coordinate] Pulling latest to detect conflicts before committing..."
  git pull --rebase 2>/dev/null || {
    echo "[coordinate] Merge conflict detected during pull!"
    echo "[coordinate] Resolve conflicts manually, then run:"
    echo "  git add <resolved-files>"
    echo "  git commit -m \"merge: resolve conflicts between agents\""
    echo "  git push"
    echo ""
    echo "[coordinate] Conflicting files:"
    git diff --name-only --diff-filter=U 2>/dev/null || true
    exit 1
  }

  echo "[coordinate] Committing changes..."
  git commit -m "$message"

  echo "[coordinate] Pushing to remote..."
  if ! git push 2>/dev/null; then
    echo "[coordinate] Push rejected. Remote has new commits. Rebasing and retrying..."
    git pull --rebase
    git push
  fi

  echo "[coordinate] Changes pushed successfully."
}

update_timestamp() {
  local agent="$1"
  local timestamp
  timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
  echo "[coordinate] Updating timestamp for $agent to $timestamp"
}

jq_update() {
  local file="$1"
  local filter="$2"
  local tmp_file="${file}.tmp"
  jq "$filter" "$file" > "$tmp_file" && mv "$tmp_file" "$file"
}

read_agent_field() {
  local agent="$1"
  local field="$2"
  jq -r ".agents.\"$agent\".\"$field\" // \"\"" "$STATUS_FILE" 2>/dev/null || echo ""
}

# ---------- Actions ----------

case "$ACTION" in
  claim)
    FILE_PATH="${3:-}"
    PURPOSE="${4:-}"
    if [ -z "$AGENT_NAME" ] || [ -z "$FILE_PATH" ]; then
      echo "Usage: $0 claim <agent-name> <file-path> <purpose>"
      exit 1
    fi

    pull_latest

    # Check if file is already locked
    LOCKED_BY=$(jq -r ".file_locks.\"$FILE_PATH\".locked_by // \"\"" "$STATUS_FILE" 2>/dev/null)
    if [ -n "$LOCKED_BY" ] && [ "$LOCKED_BY" != "$AGENT_NAME" ]; then
      LOCK_PURPOSE=$(jq -r ".file_locks.\"$FILE_PATH\".purpose // \"\"" "$STATUS_FILE")
      LOCK_TIME=$(jq -r ".file_locks.\"$FILE_PATH\".locked_at // \"\"" "$STATUS_FILE")
      echo "[coordinate] ERROR: File '$FILE_PATH' is already locked by '$LOCKED_BY'"
      echo "[coordinate] Purpose: $LOCK_PURPOSE"
      echo "[coordinate] Locked at: $LOCK_TIME"
      echo ""
      echo "[coordinate] You must coordinate with $LOCKED_BY before editing this file."
      echo "[coordinate] Check '$STATUS_FILE' agent_notes section for messages."
      exit 1
    fi

    # Check if file is already claimed by this agent
    WORKING_ON=$(read_agent_field "$AGENT_NAME" "working_on" | jq -r '. // [] | join(" ")')
    if echo "$WORKING_ON" | grep -q "$FILE_PATH"; then
      echo "[coordinate] Warning: '$FILE_PATH' is already claimed by $AGENT_NAME. Updating lock."
    fi

    # Update status.json
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    jq_update "$STATUS_FILE" "
      .last_updated = \"$TIMESTAMP\"
      | .agents.\"$AGENT_NAME\".status = \"busy\"
      | .agents.\"$AGENT_NAME\".current_task = \"$PURPOSE\"
      | .agents.\"$AGENT_NAME\".last_active = \"$TIMESTAMP\"
      | .agents.\"$AGENT_NAME\".working_on = (.agents.\"$AGENT_NAME\".working_on + [\"$FILE_PATH\"] | unique)
      | .file_locks.\"$FILE_PATH\" = {
          \"locked_by\": \"$AGENT_NAME\",
          \"locked_at\": \"$TIMESTAMP\",
          \"purpose\": \"$PURPOSE\",
          \"status\": \"in_progress\"
        }
    "

    push_changes "$AGENT_NAME" "coordinate: $AGENT_NAME claimed $FILE_PATH for $PURPOSE"
    echo "[coordinate] File '$FILE_PATH' claimed successfully."
    ;;

  release)
    FILE_PATH="${3:-}"
    if [ -z "$AGENT_NAME" ] || [ -z "$FILE_PATH" ]; then
      echo "Usage: $0 release <agent-name> <file-path>"
      exit 1
    fi

    pull_latest

    # Remove file from agent's working_on list and remove lock
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    jq_update "$STATUS_FILE" "
      .last_updated = \"$TIMESTAMP\"
      | .agents.\"$AGENT_NAME\".working_on = (.agents.\"$AGENT_NAME\".working_on - [\"$FILE_PATH\"])
      | if (.agents.\"$AGENT_NAME\".working_on | length) == 0 then
          .agents.\"$AGENT_NAME\".status = \"idle\"
          | .agents.\"$AGENT_NAME\".current_task = \"\"
        else . end
      | .agents.\"$AGENT_NAME\".last_active = \"$TIMESTAMP\"
      | del(.file_locks.\"$FILE_PATH\")
    "

    push_changes "$AGENT_NAME" "coordinate: $AGENT_NAME released $FILE_PATH"
    echo "[coordinate] File '$FILE_PATH' released."
    ;;

  push)
    COMMIT_MSG="${3:-}"
    if [ -z "$AGENT_NAME" ] || [ -z "$COMMIT_MSG" ]; then
      echo "Usage: $0 push <agent-name> <commit-message>"
      exit 1
    fi

    # Update heartbeat before push
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    if [ -f "$STATUS_FILE" ]; then
      jq_update "$STATUS_FILE" "
        .last_updated = \"$TIMESTAMP\"
        | .agents.\"$AGENT_NAME\".last_active = \"$TIMESTAMP\"
      " 2>/dev/null || true
    fi

    push_changes "$AGENT_NAME" "$COMMIT_MSG"
    ;;

  heartbeat)
    if [ -z "$AGENT_NAME" ]; then
      echo "Usage: $0 heartbeat <agent-name>"
      exit 1
    fi

    pull_latest

    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    jq_update "$STATUS_FILE" "
      .last_updated = \"$TIMESTAMP\"
      | .agents.\"$AGENT_NAME\".last_active = \"$TIMESTAMP\"
    "

    push_changes "$AGENT_NAME" "coordinate: heartbeat $AGENT_NAME"
    echo "[coordinate] Heartbeat sent for $AGENT_NAME."
    ;;

  share)
    FILE_PATH="${3:-}"
    if [ -z "$AGENT_NAME" ] || [ -z "$FILE_PATH" ]; then
      echo "Usage: $0 share <agent-name> <file-path>"
      exit 1
    fi

    pull_latest

    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    jq_update "$STATUS_FILE" "
      .last_updated = \"$TIMESTAMP\"
      | .agents.\"$AGENT_NAME\".last_active = \"$TIMESTAMP\"
      | .shared_files.\"$FILE_PATH\".working_agents = ((.shared_files.\"$FILE_PATH\".working_agents // []) + [\"$AGENT_NAME\"] | unique)
      | if .shared_files.\"$FILE_PATH\".last_merge == null then
          .shared_files.\"$FILE_PATH\".last_merge = \"$TIMESTAMP\"
        else . end
      | .shared_files.\"$FILE_PATH\".merge_required = false
    "

    push_changes "$AGENT_NAME" "coordinate: $AGENT_NAME marked $FILE_PATH as shared"
    echo "[coordinate] File '$FILE_PATH' marked as shared."
    ;;

  note)
    NOTE="${3:-}"
    if [ -z "$AGENT_NAME" ] || [ -z "$NOTE" ]; then
      echo "Usage: $0 note <agent-name> <message>"
      exit 1
    fi

    pull_latest

    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    ESCAPED_NOTE=$(echo "$NOTE" | sed 's/"/\\"/g')
    jq_update "$STATUS_FILE" "
      .last_updated = \"$TIMESTAMP\"
      | .agents.\"$AGENT_NAME\".last_active = \"$TIMESTAMP\"
      | .agent_notes.\"$AGENT_NAME\" = \"[$TIMESTAMP] $ESCAPED_NOTE\"
    "

    push_changes "$AGENT_NAME" "coordinate: note from $AGENT_NAME"
    echo "[coordinate] Note left for other agents."
    ;;

  status)
    if [ -f "$STATUS_FILE" ]; then
      echo "=== AI Coordination Status ==="
      echo ""
      echo "--- Agents ---"
      jq -r '.agents | to_entries[] | "\(.key): \(.value.status) - \(.value.current_task // "idle") (last active: \(.value.last_active))"' "$STATUS_FILE" 2>/dev/null || echo "No agents found"
      echo ""
      echo "--- File Locks ---"
      jq -r '.file_locks | to_entries[] | "\(.key) -> locked by: \(.value.locked_by) (\(.value.purpose))"' "$STATUS_FILE" 2>/dev/null || echo "No locks"
      echo ""
      echo "--- Shared Files ---"
      jq -r '.shared_files | to_entries[] | "\(.key) -> agents: \(.value.working_agents | join(", "))"' "$STATUS_FILE" 2>/dev/null || echo "No shared files"
      echo ""
      echo "--- Agent Notes ---"
      jq -r '.agent_notes | to_entries[] | "\(.key): \(.value)"' "$STATUS_FILE" 2>/dev/null || echo "No notes"
      echo ""
      echo "--- Pending Merges ---"
      jq -r '.pending_merges[] | "\(.file): \(.agents | join(" + ")) [\(.status)]"' "$STATUS_FILE" 2>/dev/null || echo "No pending merges"
    else
      echo "[coordinate] Status file not found at $STATUS_FILE"
      exit 1
    fi
    ;;

  *)
    echo "[coordinate] Unknown action: $ACTION"
    echo "Valid actions: claim, release, push, heartbeat, share, note, status"
    exit 1
    ;;
esac
