#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "argus-local-dev-scripts/deploy-local.sh is now a compatibility wrapper for deploy/deploy.sh."
echo "Using the universal incremental deploy path..."

exec "$ROOT/deploy/deploy.sh" "$@"
