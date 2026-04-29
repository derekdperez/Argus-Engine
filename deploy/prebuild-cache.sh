#!/usr/bin/env bash
# Warm Docker/NuGet/Go caches before an active debugging session.
# This is intended to be run once after cloning, after dependency changes, or before a long edit/debug loop.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

NIGHTMARE_DEPLOY_FRESH="${NIGHTMARE_DEPLOY_FRESH:-0}"
NIGHTMARE_DEPLOY_MODE=image
export NIGHTMARE_DEPLOY_FRESH NIGHTMARE_DEPLOY_MODE

# shellcheck source=deploy/lib-nightmare-compose.sh
source "$DEPLOY_DIR/lib-nightmare-compose.sh"
# shellcheck source=deploy/lib-install-deps.sh
source "$DEPLOY_DIR/lib-install-deps.sh"

nightmare_ensure_runtime_dependencies
nightmare_export_build_stamp "$ROOT"

# Build all service images once. Dockerfile cache mounts retain NuGet and Go package downloads.
NIGHTMARE_CHANGED_SERVICES="$(nightmare_all_dotnet_services | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
export NIGHTMARE_CHANGED_SERVICES

echo "Prebuilding all Nightmare v2 app images and warming NuGet/Go caches..."
nightmare_compose_build

# Also warm the hot-swap NuGet cache outside docker build layers.
mkdir -p "$ROOT/.nuget/packages"
echo "Warming hot-swap NuGet cache..."
nightmare_docker run --rm \
  --user "$(id -u):$(id -g)" \
  -v "$ROOT:/workspace" \
  -w /workspace \
  -e DOTNET_CLI_HOME=/tmp/dotnet-cli \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -e NUGET_PACKAGES=/workspace/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -lc 'for p in src/*/*.csproj; do dotnet restore "$p"; done'

nightmare_detect_changed_services "$ROOT"
nightmare_commit_current_fingerprints
nightmare_write_last_deploy_stamp

echo "Cache warm complete. Next source-only iteration can use: ./deploy/deploy.sh --hot"
