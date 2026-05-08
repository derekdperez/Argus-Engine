#!/usr/bin/env bash
# Materialize subfinder and amass binaries into deploy/artifacts/recon-tools.
#
# The generated linux-amd64 binaries can be committed so deploy/Dockerfile.base-recon
# can skip go install on fresh deployment hosts.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"

out_dir="$ROOT/deploy/artifacts/recon-tools/linux-amd64"
manifest="$ROOT/deploy/artifacts/recon-tools/manifest.txt"
image="argus-recon-base:vendor-build"

mkdir -p "$out_dir"

subfinder_package="${SUBFINDER_PACKAGE:-github.com/projectdiscovery/subfinder/v2/cmd/subfinder@v2.14.0}"
amass_package="${AMASS_PACKAGE:-github.com/owasp-amass/amass/v5/cmd/amass@v5.1.1}"

echo "Building recon tools once from pinned Go packages..."
argus_docker build \
  --build-arg ARGUS_RECON_VENDOR_MODE=off \
  --build-arg "SUBFINDER_PACKAGE=$subfinder_package" \
  --build-arg "AMASS_PACKAGE=$amass_package" \
  -t "$image" \
  -f deploy/Dockerfile.base-recon \
  deploy/

container_id="$(argus_docker create "$image")"
cleanup() {
  argus_docker rm -f "$container_id" >/dev/null 2>&1 || true
}
trap cleanup EXIT

argus_docker cp "$container_id:/subfinder" "$out_dir/subfinder"
argus_docker cp "$container_id:/amass" "$out_dir/amass"
chmod +x "$out_dir/subfinder" "$out_dir/amass"

subfinder_sha="$(sha256sum "$out_dir/subfinder" | awk '{print $1}')"
amass_sha="$(sha256sum "$out_dir/amass" | awk '{print $1}')"

{
  echo "generated_at_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "target=linux-amd64"
  echo "subfinder_package=$subfinder_package"
  echo "subfinder_sha256=$subfinder_sha"
  echo "amass_package=$amass_package"
  echo "amass_sha256=$amass_sha"
} >"$manifest"

echo "Wrote $manifest"
echo "Vendored recon binaries are in deploy/artifacts/recon-tools/linux-amd64"
