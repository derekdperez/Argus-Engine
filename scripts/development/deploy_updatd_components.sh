#!/usr/bin/env bash
# Compatibility wrapper for the misspelled script name requested during MVP triage.
set -Eeuo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/deploy_updated_components.sh" "$@"
