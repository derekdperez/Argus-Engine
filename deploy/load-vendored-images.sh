#!/usr/bin/env bash
# Load repo-vendored Docker image tarballs when they are present.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"

loaded=0

load_image() {
  local image_name="$1"
  local tarball="$2"

  if argus_docker image inspect "$image_name" >/dev/null 2>&1; then
    return 0
  fi

  if [[ -f "$tarball" ]]; then
    echo "Loading vendored image: $tarball"
    gzip -dc "$tarball" | argus_docker load
    loaded=1
  fi
}

load_image "argus-engine-base:local" "$ROOT/deploy/artifacts/images/argus-engine-base.local.tar.gz"
load_image "argus-recon-base:local" "$ROOT/deploy/artifacts/images/argus-recon-base.local.tar.gz"

if [[ "$loaded" == "0" ]]; then
  echo "No vendored base image tarballs were loaded."
fi
