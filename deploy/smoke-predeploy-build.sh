#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PROJECT="src/ArgusEngine.CommandCenter.Web/ArgusEngine.CommandCenter.Web.csproj"
OUT_DIR="$ROOT/deploy/.smoke-predeploy/command-center-web"
VERSION="${ARGUS_ENGINE_VERSION:-local-smoke}"
ASSEMBLY_VERSION="${ARGUS_ASSEMBLY_VERSION:-2.6.3.0}"
FILE_VERSION="${ARGUS_FILE_VERSION:-2.6.3.0}"

echo "Running predeploy smoke publish for Command Center Web..."
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

dotnet publish "$PROJECT" \
  -c Release \
  -o "$OUT_DIR" \
  /p:UseAppHost=false \
  /p:Version="$VERSION" \
  /p:AssemblyVersion="$ASSEMBLY_VERSION" \
  /p:FileVersion="$FILE_VERSION" \
  /p:InformationalVersion="$VERSION"

test -s "$OUT_DIR/ArgusEngine.CommandCenter.Web.dll"
echo "Predeploy smoke publish passed."
