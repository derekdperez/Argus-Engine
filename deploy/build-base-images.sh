#!/usr/bin/env bash
# Build and tag Argus Engine base images to speed up downstream service builds.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"

echo "Building Argus Engine Runtime Base image..."
argus_docker build -t argus-engine-base:local -f deploy/Dockerfile.base-runtime deploy/

echo "Building Argus Recon Tools Base image..."
argus_docker build -t argus-recon-base:local -f deploy/Dockerfile.base-recon deploy/

echo "Base images ready: argus-engine-base:local, argus-recon-base:local"
