#!/usr/bin/env bash
# Safe local/EC2 deployment wrapper for Argus Engine.
#
# Why this exists:
#   deploy/deploy.sh can ask Docker Compose to build many service images in one invocation.
#   On small EC2 hosts that often looks "hung" because BuildKit/Compose multiplex output and
#   CPU/RAM/disk are saturated. This wrapper keeps the existing deploy flow, but disables the
#   TUI and forces per-service builds with plain output so progress and failures are visible.
#
# Usage:
#   ./deploy/deploy-batched.sh
#   ./deploy/deploy-batched.sh --image
#   ./deploy/deploy-batched.sh -fresh
#
# Useful overrides:
#   argus_BUILD_TIMEOUT_MIN=90 ./deploy/deploy-batched.sh --image
#   ARGUS_LOCAL_LOG_TAIL=400 ./deploy/deploy-batched.sh logs
set -Eeuo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

if [[ ! -f "$DEPLOY_DIR/deploy.sh" ]]; then
  echo "ERROR: expected $DEPLOY_DIR/deploy.sh to exist." >&2
  echo "Run this from an Argus Engine checkout after unzipping the overlay at the repo root." >&2
  exit 2
fi

# Do not launch the Python TUI. It can hide BuildKit output during long builds.
export ARGUS_NO_UI="${ARGUS_NO_UI:-1}"

# Build one selected service image at a time. The existing deploy library supports this.
export argus_BUILD_SEQUENTIAL="${argus_BUILD_SEQUENTIAL:-1}"

# Show plain BuildKit progress instead of a TTY progress renderer.
export argus_BUILD_PROGRESS="${argus_BUILD_PROGRESS:-plain}"
export BUILDKIT_PROGRESS="${BUILDKIT_PROGRESS:-plain}"

# Keep Compose from using bake unless explicitly requested.
export COMPOSE_BAKE="${COMPOSE_BAKE:-false}"

# Fail loudly if a single service build makes no completion for too long.
# 0 disables timeout. Increase this if worker-enum is cold-building recon tools.
export argus_BUILD_TIMEOUT_MIN="${argus_BUILD_TIMEOUT_MIN:-45}"

# deploy.sh currently sets COMPOSE_PARALLEL_LIMIT internally, but sequential build mode
# still prevents the 17 app images from being built in one Compose invocation.
export COMPOSE_PARALLEL_LIMIT="${COMPOSE_PARALLEL_LIMIT:-1}"

echo "Argus batched deploy wrapper"
echo "  repo: $ROOT"
echo "  ARGUS_NO_UI=$ARGUS_NO_UI"
echo "  argus_BUILD_SEQUENTIAL=$argus_BUILD_SEQUENTIAL"
echo "  argus_BUILD_PROGRESS=$argus_BUILD_PROGRESS"
echo "  argus_BUILD_TIMEOUT_MIN=$argus_BUILD_TIMEOUT_MIN"
echo "  COMPOSE_BAKE=$COMPOSE_BAKE"
echo ""
echo "Continuing via deploy/deploy.sh $*"
echo ""

exec "$DEPLOY_DIR/deploy.sh" "$@"
