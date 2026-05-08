#!/usr/bin/env bash
# Materialize subfinder and amass release binaries into deploy/artifacts/recon-tools.
#
# The generated linux-amd64 binaries can be committed so deploy/Dockerfile.base-recon
# can skip all network work on fresh deployment hosts.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

out_dir="$ROOT/deploy/artifacts/recon-tools/linux-amd64"
manifest="$ROOT/deploy/artifacts/recon-tools/manifest.txt"
work_dir="$ROOT/deploy/artifacts/recon-tools/.download"

mkdir -p "$out_dir" "$work_dir"

subfinder_version="${SUBFINDER_VERSION:-2.14.0}"
amass_version="${AMASS_VERSION:-5.1.1}"
subfinder_archive="subfinder_${subfinder_version}_linux_amd64.zip"
amass_archive="amass_linux_amd64.tar.gz"

download_and_verify() {
  local archive="$1"
  local archive_url="$2"
  local checksums_url="$3"
  local checksums_file="$work_dir/${archive}.checksums.txt"

  echo "Downloading $archive"
  curl -fsSL "$archive_url" -o "$work_dir/$archive"
  curl -fsSL "$checksums_url" -o "$checksums_file"

  if grep -F "$archive" "$checksums_file" >"$work_dir/${archive}.sha256"; then
    (cd "$work_dir" && sha256sum -c "${archive}.sha256")
  else
    echo "Could not find checksum for $archive in $checksums_url" >&2
    exit 1
  fi
}

download_and_verify \
  "$subfinder_archive" \
  "https://github.com/projectdiscovery/subfinder/releases/download/v${subfinder_version}/${subfinder_archive}" \
  "https://github.com/projectdiscovery/subfinder/releases/download/v${subfinder_version}/subfinder_${subfinder_version}_checksums.txt"

download_and_verify \
  "$amass_archive" \
  "https://github.com/owasp-amass/amass/releases/download/v${amass_version}/${amass_archive}" \
  "https://github.com/owasp-amass/amass/releases/download/v${amass_version}/amass_checksums.txt"

rm -rf "$work_dir/extract"
mkdir -p "$work_dir/extract/subfinder" "$work_dir/extract/amass"
unzip -q "$work_dir/$subfinder_archive" -d "$work_dir/extract/subfinder"
tar -xzf "$work_dir/$amass_archive" -C "$work_dir/extract/amass"

find "$work_dir/extract/subfinder" -type f -name subfinder -exec cp '{}' "$out_dir/subfinder" ';'
find "$work_dir/extract/amass" -type f -path '*/amass' -exec cp '{}' "$out_dir/amass" ';'
chmod +x "$out_dir/subfinder" "$out_dir/amass"

subfinder_sha="$(sha256sum "$out_dir/subfinder" | awk '{print $1}')"
amass_sha="$(sha256sum "$out_dir/amass" | awk '{print $1}')"

{
  echo "generated_at_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "target=linux-amd64"
  echo "subfinder_version=$subfinder_version"
  echo "subfinder_sha256=$subfinder_sha"
  echo "amass_version=$amass_version"
  echo "amass_sha256=$amass_sha"
} >"$manifest"

echo "Wrote $manifest"
echo "Vendored recon binaries are in deploy/artifacts/recon-tools/linux-amd64"
rm -rf "$work_dir"
