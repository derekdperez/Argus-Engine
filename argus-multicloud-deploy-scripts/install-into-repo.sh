#!/usr/bin/env bash
set -euo pipefail

target="${1:-}"
if [[ -z "$target" ]]; then
  echo "Usage: $0 /path/to/Argus-Engine" >&2
  exit 2
fi
if [[ ! -d "$target" ]]; then
  echo "Target directory does not exist: $target" >&2
  exit 2
fi
if [[ ! -f "$target/ArgusEngine.slnx" ]]; then
  echo "Target does not look like the Argus-Engine repo root: $target" >&2
  exit 2
fi

src_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$target/deploy"

cp -R "$src_dir/deploy/cloud-common.sh" "$target/deploy/cloud-common.sh"
mkdir -p "$target/deploy/cloud-tools" "$target/deploy/azure" "$target/deploy/gcp"
cp -R "$src_dir/deploy/cloud-tools/." "$target/deploy/cloud-tools/"
cp -R "$src_dir/deploy/azure/." "$target/deploy/azure/"
cp -R "$src_dir/deploy/gcp/." "$target/deploy/gcp/"

chmod +x \
  "$target/deploy/cloud-tools/install-cloud-clis.sh" \
  "$target/deploy/azure/"*.sh \
  "$target/deploy/gcp/"*.sh

echo "Installed multi-cloud deployment helpers into $target"
echo "Next:"
echo "  cp deploy/azure/.env.example deploy/azure/.env"
echo "  cp deploy/azure/service-env.example deploy/azure/service-env"
echo "  cp deploy/gcp/.env.example deploy/gcp/.env"
echo "  cp deploy/gcp/service-env.example deploy/gcp/service-env"
