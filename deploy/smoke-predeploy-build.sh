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

if command -v dotnet >/dev/null 2>&1; then
  dotnet publish "$PROJECT" \
    -c Release \
    -o "$OUT_DIR" \
    /p:UseAppHost=false \
    /p:Version="$VERSION" \
    /p:AssemblyVersion="$ASSEMBLY_VERSION" \
    /p:FileVersion="$FILE_VERSION" \
    /p:InformationalVersion="$VERSION"
else
  if ! command -v docker >/dev/null 2>&1; then
    echo "ERROR: predeploy smoke requires either dotnet or docker, but neither is available." >&2
    exit 127
  fi

  echo "dotnet not found locally; running smoke publish in Docker SDK container..."
  mkdir -p "$ROOT/deploy/.nuget-packages"
  docker run --rm \
    -v "$ROOT:/src" \
    -v "$ROOT/deploy/.nuget-packages:/root/.nuget/packages" \
    -w /src \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish "$PROJECT" \
      -c Release \
      -o "$OUT_DIR" \
      /p:UseAppHost=false \
      /p:Version="$VERSION" \
      /p:AssemblyVersion="$ASSEMBLY_VERSION" \
      /p:FileVersion="$FILE_VERSION" \
      /p:InformationalVersion="$VERSION"
fi

test -s "$OUT_DIR/ArgusEngine.CommandCenter.Web.dll"
echo "Predeploy smoke publish passed."
