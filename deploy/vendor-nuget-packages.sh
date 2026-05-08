#!/usr/bin/env bash
# Materialize a repo-local NuGet package cache for cold deployment hosts.
#
# This can create a large number of files. Commit deploy/artifacts/nuget/packages
# only when faster cold deploys are worth the repository weight.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"

packages_dir="$ROOT/deploy/artifacts/nuget/packages"
manifest="$ROOT/deploy/artifacts/nuget/manifest.txt"

mkdir -p "$packages_dir"

echo "Restoring service projects into repo-local NuGet cache:"
while IFS= read -r service; do
  csproj="$(argus_service_csproj "$service")"
  echo "  $service -> $csproj"
  NUGET_PACKAGES="$packages_dir" dotnet restore "$csproj"
done < <(argus_all_dotnet_services)

package_count="$(find "$packages_dir" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d '[:space:]')"
fingerprint="$(
  find "$packages_dir" -type f -print0 |
    LC_ALL=C sort -z |
    xargs -0 sha256sum |
    sha256sum |
    awk '{print $1}'
)"

{
  echo "generated_at_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "package_count=$package_count"
  echo "fingerprint_sha256=$fingerprint"
  echo "dotnet_sdk=$(dotnet --version)"
} >"$manifest"

echo "Wrote $manifest"
echo "Repo-local NuGet packages are in deploy/artifacts/nuget/packages"
