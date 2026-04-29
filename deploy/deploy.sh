#!/usr/bin/env bash
# One-command local / EC2 deploy for the full Nightmare v2 .NET stack (Docker Compose).
# Re-runnable: safe to run again.
#
# Default (incremental): fingerprints each app service and rebuilds only the service images whose
# own source/shared dependencies changed. It does not pull base images or force-recreate unchanged
# containers on every deploy. Use -fresh to always rebuild all images with --no-cache.
#
# Dependencies (Linux): Docker Engine + Compose are installed automatically via get.docker.com and
# your distro package manager when missing (requires sudo). Set NIGHTMARE_SKIP_INSTALL=1 to only verify.
# macOS / Windows: install Docker Desktop yourself, then re-run.
#
# Requires either (after bootstrap):
#   - Docker Compose V2:  "docker compose version" works, or
#   - Docker Compose V1:  standalone "docker-compose" on PATH.
#
# Optional environment:
#   NIGHTMARE_GIT_PULL=1   Run git pull --ff-only in the repo before building (remote must be ff-only).
#   NIGHTMARE_NO_CACHE=1      docker compose build --no-cache (also implied by -fresh)
#   NIGHTMARE_PULL_IMAGES=1    docker compose build --pull. Defaults to 0 for fast deploys.
#   NIGHTMARE_FORCE_RECREATE=1 Force recreate containers. Defaults to 0 for fast deploys.
#   NIGHTMARE_SKIP_INSTALL=1   Do not install Docker / curl / git; fail if docker or compose is missing.
#   NIGHTMARE_DEPLOY_FRESH=1   Same as passing -fresh on the command line.
#   SUBFINDER_PACKAGE=...  Optional go install package for the worker image subfinder binary.
#   AMASS_PACKAGE=...      Optional go install package for the worker image amass binary.
#   COMPOSE_BAKE=true|false    Multi-service compose builds may use "bake"; scripts default to false for stability.
#
# If you see: unknown shorthand flag: 'd' in -d
#   you ran "docker compose ..." without the Compose plugin — "compose" was ignored and
#   "up -d" was parsed as invalid global docker flags. Install the plugin or use docker-compose.
set -euo pipefail

# Resolve absolute paths before cd: dirname of ./deploy.sh is ".", so a relative
# lib path would wrongly resolve against the post-cd working directory.
DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

NIGHTMARE_DEPLOY_FRESH="${NIGHTMARE_DEPLOY_FRESH:-0}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    -fresh | --fresh)
      NIGHTMARE_DEPLOY_FRESH=1
      shift
      ;;
    -h | --help)
      cat <<'EOF'
Usage: ./deploy/deploy.sh [-fresh]

  (default)  Fast incremental: rebuild only service image(s) whose source/shared
             dependency fingerprint changed, then run compose up without forcing
             unchanged containers to restart.

  -fresh     Rebuild all service images with --pull --no-cache and force recreate.

Environment:
  NIGHTMARE_GIT_PULL=1
  NIGHTMARE_SKIP_INSTALL=1
  NIGHTMARE_DEPLOY_FRESH=1
  NIGHTMARE_NO_CACHE=1
  NIGHTMARE_PULL_IMAGES=1
  NIGHTMARE_FORCE_RECREATE=1
EOF
      exit 0
      ;;
    *)
      echo "Unknown argument: $1 (use -h for help)" >&2
      exit 1
      ;;
  esac
done
export NIGHTMARE_DEPLOY_FRESH

# shellcheck source=deploy/lib-nightmare-compose.sh
source "$DEPLOY_DIR/lib-nightmare-compose.sh"
# shellcheck source=deploy/lib-install-deps.sh
source "$DEPLOY_DIR/lib-install-deps.sh"

nightmare_ensure_runtime_dependencies

nightmare_maybe_git_pull "$ROOT"
nightmare_export_build_stamp "$ROOT"
nightmare_decide_incremental_deploy

echo "Applying stack from: $ROOT"
nightmare_compose_full_redeploy

echo ""
echo "Nightmare v2 is running."
echo "  Command Center:  http://localhost:8080/  (use host public IP on EC2)"
echo "  RabbitMQ admin:  http://localhost:15672/  (user/pass: nightmare / nightmare)"
echo "  Postgres:        localhost:5432  db=nightmare_v2 (+ file blobs db nightmare_v2_files)  user=nightmare"
echo ""
echo "Subdomain enumeration tools are installed into the worker images:"
echo "  docker compose -f deploy/docker-compose.yml run --rm --entrypoint sh worker-enum -c 'command -v subfinder && command -v amass && test -s /opt/nightmare/wordlists/subdomains.txt'"
echo ""
echo "Useful commands (from $ROOT):"
echo "  ./deploy/smoke-test.sh                    # verify health, Blazor static assets, and dependency diagnostics"
echo "  ./deploy/logs.sh --errors                 # show recent error-like log lines across services"
echo "  ./deploy/logs.sh --follow worker-spider   # follow one service"
echo "  docker compose -f deploy/docker-compose.yml down"
echo "(or docker-compose -f deploy/docker-compose.yml ... if you use V1)"
echo ""
