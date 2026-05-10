#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="${1:-$PWD}"

if [[ ! -f "$REPO_ROOT/ArgusEngine.slnx" ]]; then
  echo "Usage: ./install-argus-azure-deploy-overlay.sh /path/to/argus-engine" >&2
  echo "Or run it from the Argus repo root." >&2
  exit 2
fi

OVERLAY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

mkdir -p "$REPO_ROOT/deploy/azure" "$REPO_ROOT/deploy/cloud-tools"

cp "$OVERLAY_DIR/deploy/cloud-common.sh" "$REPO_ROOT/deploy/cloud-common.sh"
cp "$OVERLAY_DIR/deploy/cloud-tools/install-cloud-clis.sh" "$REPO_ROOT/deploy/cloud-tools/install-cloud-clis.sh"
cp "$OVERLAY_DIR/deploy/azure/"*.sh "$REPO_ROOT/deploy/azure/"
cp "$OVERLAY_DIR/deploy/azure/.env.example" "$REPO_ROOT/deploy/azure/.env.example"
cp "$OVERLAY_DIR/deploy/azure/service-env.example" "$REPO_ROOT/deploy/azure/service-env.example"
if [[ -f "$OVERLAY_DIR/deploy/azure/.env" ]]; then
  if [[ -f "$REPO_ROOT/deploy/azure/.env" ]]; then
    cp "$REPO_ROOT/deploy/azure/.env" "$REPO_ROOT/deploy/azure/.env.bak.$(date +%s)"
  fi
  cp "$OVERLAY_DIR/deploy/azure/.env" "$REPO_ROOT/deploy/azure/.env"
fi

chmod +x "$REPO_ROOT/deploy/cloud-common.sh" "$REPO_ROOT/deploy/cloud-tools/install-cloud-clis.sh" "$REPO_ROOT/deploy/azure/"*.sh

echo "Installed configured Azure deploy scripts into: $REPO_ROOT/deploy/azure"
echo
echo "Next:"
echo "  cd \"$REPO_ROOT\""
echo "  ./deploy/azure/doctor.sh"
echo "  ./deploy/cloud-tools/install-cloud-clis.sh --azure --login"
echo "  ./deploy/azure/create-containerapps-resources.sh"
echo "  ./deploy/azure/build-push-acr.sh"
echo "  ./deploy/azure/deploy-containerapps-workers.sh"
