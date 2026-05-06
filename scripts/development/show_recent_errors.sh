#!/usr/bin/env bash
# Convenience wrapper for recent error-like logs across the local Argus stack.

set -Eeuo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/show_development_machine_logs.sh" --no-machine --errors "$@"
