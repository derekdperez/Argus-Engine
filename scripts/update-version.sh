#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

usage() {
  cat <<'USAGE'
Usage: scripts/update-version.sh [--version X.Y.Z] [--build-time RFC3339] [--no-stamp]

Synchronize version metadata across deployment scripts, project files, and version.json.
USAGE
}

VERSION_OVERRIDE=""
BUILD_TIME_OVERRIDE=""
STAMP=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || { echo "ERROR: --version requires a value" >&2; exit 1; }
      VERSION_OVERRIDE="$2"
      shift 2
      ;;
    --build-time)
      [[ $# -ge 2 ]] || { echo "ERROR: --build-time requires a value" >&2; exit 1; }
      BUILD_TIME_OVERRIDE="$2"
      shift 2
      ;;
    --no-stamp)
      STAMP=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "ERROR: unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

VERSION_VALUE="${VERSION_OVERRIDE:-$(tr -d '[:space:]' < "$ROOT/VERSION" 2>/dev/null || true)}"
[[ -n "$VERSION_VALUE" ]] || { echo "ERROR: VERSION is empty" >&2; exit 1; }
[[ "$VERSION_VALUE" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "ERROR: version must be X.Y.Z; got '$VERSION_VALUE'" >&2; exit 1; }

ASSEMBLY_VERSION="${VERSION_VALUE}.0"
BUILD_TIME="${BUILD_TIME_OVERRIDE:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
COMMIT_SHA="unknown"
if [[ -d "$ROOT/.git" ]]; then
  COMMIT_SHA="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
fi

printf '%s\n' "$VERSION_VALUE" > "$ROOT/VERSION"

sed -E -i \
  -e "s#<ArgusEngineDeploymentVersion>[^<]+</ArgusEngineDeploymentVersion>#<ArgusEngineDeploymentVersion>${VERSION_VALUE}</ArgusEngineDeploymentVersion>#g" \
  -e "s#<Version>[^<]+</Version>#<Version>${VERSION_VALUE}</Version>#g" \
  -e "s#<PackageVersion>[^<]+</PackageVersion>#<PackageVersion>${VERSION_VALUE}</PackageVersion>#g" \
  -e "s#<AssemblyVersion>[^<]+</AssemblyVersion>#<AssemblyVersion>${ASSEMBLY_VERSION}</AssemblyVersion>#g" \
  -e "s#<FileVersion>[^<]+</FileVersion>#<FileVersion>${ASSEMBLY_VERSION}</FileVersion>#g" \
  -e "s#<InformationalVersion>[^<]+</InformationalVersion>#<InformationalVersion>${VERSION_VALUE}</InformationalVersion>#g" \
  "$ROOT/Directory.Build.targets"

while IFS= read -r csproj; do
  sed -E -i "s#<Version>[^<]+</Version>#<Version>${VERSION_VALUE}</Version>#g" "$csproj"
done < <(find "$ROOT/src" -type f -name '*.csproj' | sort)

for file in \
  "$ROOT/deploy/Dockerfile.web" \
  "$ROOT/deploy/Dockerfile.worker" \
  "$ROOT/deploy/Dockerfile.worker-enum" \
  "$ROOT/deploy/Dockerfile.commandcenter-host"; do
  sed -E -i \
    -e "s#ARG COMPONENT_VERSION=[0-9]+\.[0-9]+\.[0-9]+#ARG COMPONENT_VERSION=${VERSION_VALUE}#g" \
    -e "s#/p:AssemblyVersion=\"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\"#/p:AssemblyVersion=\"${ASSEMBLY_VERSION}\"#g" \
    -e "s#/p:FileVersion=\"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\"#/p:FileVersion=\"${ASSEMBLY_VERSION}\"#g" \
    "$file"
done

sed -E -i \
  -e "s#(ARGUS_ASSEMBLY_VERSION:-)[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+#\\1${ASSEMBLY_VERSION}#g" \
  -e "s#(ARGUS_FILE_VERSION:-)[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+#\\1${ASSEMBLY_VERSION}#g" \
  "$ROOT/deploy/smoke-predeploy-build.sh"

sed -E -i "s#\$\{ARGUS_ENGINE_VERSION:-[^}]+\}#\$\{ARGUS_ENGINE_VERSION:-${VERSION_VALUE}\}#g" "$ROOT/deploy/docker-compose.yml"
sed -E -i "s#COMPONENT_VERSION=\$\{ARGUS_ENGINE_VERSION:-[^}]+\}#COMPONENT_VERSION=\$\{ARGUS_ENGINE_VERSION:-${VERSION_VALUE}\}#g" "$ROOT/deploy/cloud-common.sh"
if [[ -f "$ROOT/argus-multicloud-deploy-scripts/deploy/cloud-common.sh" ]]; then
  sed -E -i "s#COMPONENT_VERSION=\$\{ARGUS_ENGINE_VERSION:-[^}]+\}#COMPONENT_VERSION=\$\{ARGUS_ENGINE_VERSION:-${VERSION_VALUE}\}#g" "$ROOT/argus-multicloud-deploy-scripts/deploy/cloud-common.sh"
fi

sed -E -i \
  -e "s#^ARGUS_ENGINE_VERSION=.*#ARGUS_ENGINE_VERSION=${VERSION_VALUE}#" \
  -e "s#^BUILD_SOURCE_STAMP=.*#BUILD_SOURCE_STAMP=local-${VERSION_VALUE}#" \
  "$ROOT/deploy/.env.version.example"

sed -E -i "s#^expected=\"[^\"]+\"#expected=\"${VERSION_VALUE}\"#" "$ROOT/scripts/verify-deployment-version.sh"
sed -E -i "s#^\s*\[string\]\$ExpectedVersion = \"[^\"]+\"#[string]\$ExpectedVersion = \"${VERSION_VALUE}\"#" "$ROOT/scripts/verify-deployment-version.ps1"

if [[ "$STAMP" == "1" ]]; then
  cat > "$ROOT/version.json" <<JSON
{
  "version": "${VERSION_VALUE}",
  "buildTime": "${BUILD_TIME}",
  "commit": "${COMMIT_SHA}"
}
JSON
fi

echo "Version sync complete: ${VERSION_VALUE}"
if [[ "$STAMP" == "1" ]]; then
  echo "Stamped build time: ${BUILD_TIME}"
fi
