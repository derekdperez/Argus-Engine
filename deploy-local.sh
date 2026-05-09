#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "deploy-local.sh is now a compatibility wrapper for deploy/deploy.sh."
echo "Using the universal incremental deploy path..."

exec "$SCRIPT_DIR/deploy/deploy.sh" "$@"
