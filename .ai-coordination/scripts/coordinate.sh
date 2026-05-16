#!/usr/bin/env bash
set -euo pipefail

# AI Agent Coordination Script
# Usage: bash .ai-coordination/scripts/coordinate.sh <action> [args...]
#
# Actions:
#   status
#   register  <agent> <role> <task>
#   claim     <agent> <file-path> <purpose>
#   release   <agent> <file-path>
#   push      <agent> <commit-message>
#   heartbeat <agent>
#   share     <agent> <file-path>
#   note      <agent> <message>
#   block     <agent> <reason>
#   decision  <agent> <summary> [scope] [comma-separated-files]

ACTION="${1:-}"
FINAL_DECISION_AGENT="codex"

usage() {
  cat <<'USAGE'
Usage: bash .ai-coordination/scripts/coordinate.sh <action> [args...]

Actions:
  status
  register  <agent> <role> <task>
  claim     <agent> <file-path> <purpose>
  release   <agent> <file-path>
  push      <agent> <commit-message>
  heartbeat <agent>
  share     <agent> <file-path>
  note      <agent> <message>
  block     <agent> <reason>
  decision  <agent> <summary> [scope] [comma-separated-files]

Roles:
  senior_architect
  implementation_helper
  devops_helper
USAGE
}

if [[ -z "$ACTION" ]]; then
  usage
  exit 1
fi

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "[coordinate] ERROR: required command not found: $command_name" >&2
    exit 1
  fi
}

require_command git
require_command jq

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
cd "$REPO_ROOT"

STATUS_FILE=".ai-coordination/status.json"
STATUS_SCHEMA_FILE=".ai-coordination/status.schema.json"

utc_now() {
  date -u +"%Y-%m-%dT%H:%M:%SZ"
}

has_upstream() {
  git rev-parse --abbrev-ref --symbolic-full-name '@{u}' >/dev/null 2>&1
}

pull_latest() {
  if has_upstream; then
    echo "[coordinate] Pulling latest changes from upstream..."
    git pull --rebase --autostash
  else
    echo "[coordinate] No upstream branch configured; skipping pull."
  fi
}

push_to_upstream() {
  if has_upstream; then
    echo "[coordinate] Pushing to upstream..."
    git push
  else
    echo "[coordinate] No upstream branch configured; local commit created, push skipped."
  fi
}

ensure_status_file() {
  mkdir -p .ai-coordination/scripts
  if [[ -f "$STATUS_FILE" ]]; then
    return 0
  fi

  local ts
  ts=$(utc_now)
  cat > "$STATUS_FILE" <<JSON
{
  "protocol_version": 2,
  "last_updated": "$ts",
  "role_policy": {
    "final_decision_agent": "codex",
    "active_agents": ["codex", "agent-alpha", "agent-beta"],
    "helper_agents": ["agent-alpha", "agent-beta"],
    "decision_required_for": [
      "architecture",
      "new_features",
      "cross_cutting_refactors",
      "service_boundary_changes",
      "schema_or_contract_changes",
      "deployment_topology_changes",
      "security_sensitive_changes",
      "discarding_or_reverting_another_agent_change"
    ],
    "protected_file_patterns": []
  },
  "agents": {},
  "file_locks": {},
  "shared_files": {},
  "pending_merges": [],
  "blocked_items": [],
  "decision_log": [],
  "agent_notes": {}
}
JSON
}

jq_write() {
  local filter="$1"
  local tmp_file
  tmp_file=$(mktemp "${STATUS_FILE}.XXXXXX")
  jq "$filter" "$STATUS_FILE" > "$tmp_file"
  mv "$tmp_file" "$STATUS_FILE"
}

validate_json() {
  jq empty "$STATUS_FILE" >/dev/null
}

agent_exists_or_register_minimal() {
  local agent="$1"
  local role="${2:-unknown}"
  local task="${3:-}"
  local ts
  ts=$(utc_now)
  jq --arg agent "$agent" --arg role "$role" --arg task "$task" --arg ts "$ts" '
    .last_updated = $ts
    | .agents[$agent] = (
        (.agents[$agent] // {})
        + {
            "role": ((.agents[$agent].role // $role)),
            "status": ((.agents[$agent].status // "idle")),
            "current_task": ((if $task == "" then (.agents[$agent].current_task // "") else $task end)),
            "last_active": $ts,
            "working_on": ((.agents[$agent].working_on // []))
          }
      )
  ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
}

commit_and_push() {
  local agent="$1"
  local message="$2"

  validate_json
  git add -A

  if git diff --cached --quiet; then
    echo "[coordinate] No changes to commit."
    return 0
  fi

  echo "[coordinate] Committing: $message"
  git commit -m "$message"

  if has_upstream; then
    echo "[coordinate] Rebasing committed changes onto upstream..."
    if ! git pull --rebase --autostash; then
      echo "[coordinate] ERROR: conflict during rebase." >&2
      echo "[coordinate] Resolve conflicts manually, preserve other agents' work, then run:" >&2
      echo "  git status --short" >&2
      echo "  git diff" >&2
      echo "  git add <resolved-files>" >&2
      echo "  git rebase --continue" >&2
      echo "  git push" >&2
      echo "[coordinate] Conflicting files:" >&2
      git diff --name-only --diff-filter=U 2>/dev/null || true
      exit 1
    fi
  fi

  push_to_upstream
}

is_helper_agent() {
  local agent="$1"
  [[ "$agent" != "$FINAL_DECISION_AGENT" ]]
}

is_protected_path() {
  local path="$1"
  case "$path" in
    *.sln|*.slnx|*.csproj) return 0 ;;
    Directory.Build.props|Directory.Build.targets|Directory.Packages.props) return 0 ;;
    src/*Contracts*/*|src/*Contracts*/**|src/ArgusEngine.Domain/*|src/ArgusEngine.Domain/**) return 0 ;;
    src/*/Migrations/*|src/**/Migrations/**) return 0 ;;
    src/ArgusEngine.CommandCenter.Gateway/*|src/ArgusEngine.CommandCenter.Gateway/**) return 0 ;;
    src/ArgusEngine.Application/*|src/ArgusEngine.Application/**) return 0 ;;
    src/ArgusEngine.Infrastructure/*/Messaging/*|src/ArgusEngine.Infrastructure/**/Messaging/**) return 0 ;;
    */Program.cs|Program.cs|*/Startup*.cs|Startup*.cs) return 0 ;;
    deployment/docker-compose*.yml|deployment/gcp/*|deployment/gcp/**) return 0 ;;
    deploy.py|*/appsettings*.json|appsettings*.json) return 0 ;;
    *) return 1 ;;
  esac
}

has_codex_decision_for_file() {
  local path="$1"
  jq -e --arg file "$path" '
    any(.decision_log[]?;
      (.agent == "codex")
      and ((.files // []) | index($file) != null or (.scope // "") == "architecture" or (.scope // "") == "protected-file")
    )
  ' "$STATUS_FILE" >/dev/null 2>&1
}

print_status() {
  ensure_status_file
  validate_json
  echo "=== AI Coordination Status ==="
  echo ""
  echo "Protocol: $(jq -r '.protocol_version // "unknown"' "$STATUS_FILE")"
  echo "Final decision agent: $(jq -r '.role_policy.final_decision_agent // "codex"' "$STATUS_FILE")"
  echo "Last updated: $(jq -r '.last_updated // "unknown"' "$STATUS_FILE")"
  echo ""
  echo "--- Agents ---"
  jq -r '
    (.agents // {})
    | to_entries[]?
    | "\(.key): role=\(.value.role // "unknown") status=\(.value.status // "unknown") task=\(.value.current_task // "") last_active=\(.value.last_active // "unknown") files=\((.value.working_on // []) | join(","))"
  ' "$STATUS_FILE"
  echo ""
  echo "--- File Locks ---"
  jq -r '
    (.file_locks // {})
    | to_entries[]?
    | "\(.key) -> \(.value.locked_by) [\(.value.status // "unknown")] \(.value.purpose // "")"
  ' "$STATUS_FILE"
  echo ""
  echo "--- Shared Files ---"
  jq -r '
    (.shared_files // {})
    | to_entries[]?
    | "\(.key) -> \((.value.working_agents // []) | join(", "))"
  ' "$STATUS_FILE"
  echo ""
  echo "--- Blocks ---"
  jq -r '
    (.blocked_items // [])[]?
    | "\(.created_at) \(.agent): \(.reason) [\(.status)]"
  ' "$STATUS_FILE"
  echo ""
  echo "--- Recent Decisions ---"
  jq -r '
    (.decision_log // [])[-10:][]?
    | "\(.created_at) \(.agent): [\(.scope)] \(.summary) files=\((.files // []) | join(","))"
  ' "$STATUS_FILE"
  echo ""
  echo "--- Notes ---"
  jq -r '
    (.agent_notes // {})
    | to_entries[]?
    | "\(.key): \(.value)"
  ' "$STATUS_FILE"
}

case "$ACTION" in
  status)
    print_status
    ;;

  register)
    AGENT="${2:-}"
    ROLE="${3:-}"
    TASK="${4:-}"
    if [[ -z "$AGENT" || -z "$ROLE" || -z "$TASK" ]]; then
      echo "Usage: $0 register <agent> <role> <task>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    agent_exists_or_register_minimal "$AGENT" "$ROLE" "$TASK"
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg role "$ROLE" --arg task "$TASK" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {
          "role": $role,
          "status": "busy",
          "current_task": $task,
          "last_active": $ts,
          "working_on": (.agents[$agent].working_on // [])
        })
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "coordinate: register $AGENT"
    ;;

  claim)
    AGENT="${2:-}"
    FILE_PATH="${3:-}"
    PURPOSE="${4:-}"
    if [[ -z "$AGENT" || -z "$FILE_PATH" || -z "$PURPOSE" ]]; then
      echo "Usage: $0 claim <agent> <file-path> <purpose>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    validate_json

    LOCKED_BY=$(jq -r --arg file "$FILE_PATH" '.file_locks[$file].locked_by // ""' "$STATUS_FILE")
    if [[ -n "$LOCKED_BY" && "$LOCKED_BY" != "$AGENT" ]]; then
      echo "[coordinate] ERROR: file is locked by $LOCKED_BY: $FILE_PATH" >&2
      jq -r --arg file "$FILE_PATH" '.file_locks[$file]' "$STATUS_FILE" >&2
      exit 1
    fi

    if is_helper_agent "$AGENT" && is_protected_path "$FILE_PATH" && ! has_codex_decision_for_file "$FILE_PATH"; then
      echo "[coordinate] ERROR: helper agent cannot claim protected file without Codex decision: $FILE_PATH" >&2
      echo "[coordinate] Ask Codex to run: coordinate.sh decision codex \"<decision>\" protected-file $FILE_PATH" >&2
      exit 1
    fi

    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg file "$FILE_PATH" --arg purpose "$PURPOSE" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {
          "status": "busy",
          "current_task": $purpose,
          "last_active": $ts,
          "working_on": (((.agents[$agent].working_on // []) + [$file]) | unique)
        })
      | .file_locks[$file] = {
          "locked_by": $agent,
          "locked_at": $ts,
          "purpose": $purpose,
          "status": "in_progress"
        }
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"

    commit_and_push "$AGENT" "coordinate: $AGENT claimed $FILE_PATH"
    ;;

  release)
    AGENT="${2:-}"
    FILE_PATH="${3:-}"
    if [[ -z "$AGENT" || -z "$FILE_PATH" ]]; then
      echo "Usage: $0 release <agent> <file-path>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    validate_json

    LOCKED_BY=$(jq -r --arg file "$FILE_PATH" '.file_locks[$file].locked_by // ""' "$STATUS_FILE")
    if [[ -n "$LOCKED_BY" && "$LOCKED_BY" != "$AGENT" && "$AGENT" != "$FINAL_DECISION_AGENT" ]]; then
      echo "[coordinate] ERROR: only $LOCKED_BY or $FINAL_DECISION_AGENT can release $FILE_PATH" >&2
      exit 1
    fi

    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg file "$FILE_PATH" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent].last_active = $ts
      | .agents[$agent].working_on = ((.agents[$agent].working_on // []) - [$file])
      | if ((.agents[$agent].working_on // []) | length) == 0 then
          .agents[$agent].status = "idle" | .agents[$agent].current_task = ""
        else . end
      | del(.file_locks[$file])
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"

    commit_and_push "$AGENT" "coordinate: $AGENT released $FILE_PATH"
    ;;

  push)
    AGENT="${2:-}"
    MESSAGE="${3:-}"
    if [[ -z "$AGENT" || -z "$MESSAGE" ]]; then
      echo "Usage: $0 push <agent> <commit-message>" >&2
      exit 1
    fi
    ensure_status_file
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {"last_active": $ts})
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "$MESSAGE"
    ;;

  heartbeat)
    AGENT="${2:-}"
    if [[ -z "$AGENT" ]]; then
      echo "Usage: $0 heartbeat <agent>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {"last_active": $ts})
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "coordinate: heartbeat $AGENT"
    ;;

  share)
    AGENT="${2:-}"
    FILE_PATH="${3:-}"
    if [[ -z "$AGENT" || -z "$FILE_PATH" ]]; then
      echo "Usage: $0 share <agent> <file-path>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg file "$FILE_PATH" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {"last_active": $ts})
      | .shared_files[$file] = ((.shared_files[$file] // {}) + {
          "working_agents": (((.shared_files[$file].working_agents // []) + [$agent]) | unique),
          "merge_required": true,
          "last_updated": $ts
        })
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "coordinate: $AGENT marked $FILE_PATH shared"
    ;;

  note)
    AGENT="${2:-}"
    NOTE="${3:-}"
    if [[ -z "$AGENT" || -z "$NOTE" ]]; then
      echo "Usage: $0 note <agent> <message>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg note "[$TS] $NOTE" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {"last_active": $ts})
      | .agent_notes[$agent] = $note
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "coordinate: note from $AGENT"
    ;;

  block)
    AGENT="${2:-}"
    REASON="${3:-}"
    if [[ -z "$AGENT" || -z "$REASON" ]]; then
      echo "Usage: $0 block <agent> <reason>" >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg reason "$REASON" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {
          "status": "blocked",
          "last_active": $ts
        })
      | .blocked_items = ((.blocked_items // []) + [{
          "created_at": $ts,
          "agent": $agent,
          "reason": $reason,
          "status": "open"
        }])
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "coordinate: block recorded by $AGENT"
    ;;

  decision)
    AGENT="${2:-}"
    SUMMARY="${3:-}"
    SCOPE="${4:-general}"
    FILES_CSV="${5:-}"
    if [[ -z "$AGENT" || -z "$SUMMARY" ]]; then
      echo "Usage: $0 decision <agent> <summary> [scope] [comma-separated-files]" >&2
      exit 1
    fi
    if [[ "$AGENT" != "$FINAL_DECISION_AGENT" ]]; then
      echo "[coordinate] ERROR: only $FINAL_DECISION_AGENT can record final decisions." >&2
      exit 1
    fi
    ensure_status_file
    pull_latest
    TS=$(utc_now)
    jq --arg agent "$AGENT" --arg summary "$SUMMARY" --arg scope "$SCOPE" --arg files_csv "$FILES_CSV" --arg ts "$TS" '
      .last_updated = $ts
      | .agents[$agent] = ((.agents[$agent] // {}) + {"last_active": $ts})
      | .decision_log = ((.decision_log // []) + [{
          "id": ("decision-" + ($ts | gsub("[:TZ-]"; ""))),
          "created_at": $ts,
          "agent": $agent,
          "scope": $scope,
          "summary": $summary,
          "files": (if $files_csv == "" then [] else ($files_csv | split(",") | map(gsub("^\\s+|\\s+$"; ""))) end)
        }])
    ' "$STATUS_FILE" > "${STATUS_FILE}.tmp" && mv "${STATUS_FILE}.tmp" "$STATUS_FILE"
    commit_and_push "$AGENT" "coordinate: Codex decision - $SCOPE"
    ;;

  *)
    echo "[coordinate] Unknown action: $ACTION" >&2
    usage
    exit 1
    ;;
esac
