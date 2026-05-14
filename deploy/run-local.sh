#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "deploy/run-local.sh is now a compatibility wrapper for deploy/deploy.sh."
echo "Using the universal incremental deploy path..."

exec "$DEPLOY_DIR/deploy.sh" "$@"
