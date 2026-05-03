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
#   NIGHTMARE_DEPLOY_MODE=image|hot  image=normal cached image deploy (default); hot=publish/copy/restart changed running services.
#   NIGHTMARE_GIT_PULL=1   Run git pull --ff-only in the repo before building (remote must be ff-only). Defaults to 1 for --ecs-workers.
#   NIGHTMARE_NO_CACHE=1      docker compose build --no-cache (also implied by -fresh)
#   NIGHTMARE_PULL_IMAGES=1    docker compose build --pull. Defaults to 0 for fast deploys.
#   NIGHTMARE_FORCE_RECREATE=1 Force recreate containers. Defaults to 0 for fast deploys.
#   NIGHTMARE_SKIP_INSTALL=1   Do not install Docker / curl / git; fail if docker or compose is missing.
#   NIGHTMARE_DEPLOY_FRESH=1   Same as passing -fresh on the command line.
#   NIGHTMARE_ECS_REPLACE_WORKERS=1  In --ecs-workers mode, stop existing ECS worker tasks before recreating them.
#   NIGHTMARE_SKIP_BLAZOR_ASSET_VERIFY=1  Skip post-deploy verification of /_framework/blazor.web.js.
#   SUBFINDER_PACKAGE=...  Optional go install package for the worker image subfinder binary.
#   AMASS_PACKAGE=...      Optional go install package for the worker image amass binary.
#   COMPOSE_BAKE=true|false    Multi-service compose builds may use "bake"; scripts default to false for stability.
#   NIGHTMARE_ECS_WORKERS=1     Deploy core stack locally on EC2 and deploy workers as ECS services.
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
NIGHTMARE_DEPLOY_MODE="${NIGHTMARE_DEPLOY_MODE:-image}"
NIGHTMARE_ECS_WORKERS="${NIGHTMARE_ECS_WORKERS:-0}"
NIGHTMARE_ECS_REPLACE_WORKERS="${NIGHTMARE_ECS_REPLACE_WORKERS:-1}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ecs-workers)
      NIGHTMARE_ECS_WORKERS=1
      NIGHTMARE_DEPLOY_MODE=image
      shift
      ;;
    -fresh | --fresh)
      NIGHTMARE_DEPLOY_FRESH=1
      NIGHTMARE_DEPLOY_MODE=image
      shift
      ;;
    --hot | -hot)
      NIGHTMARE_DEPLOY_MODE=hot
      shift
      ;;
    --image | -image)
      NIGHTMARE_DEPLOY_MODE=image
      shift
      ;;
    -h | --help)
      cat <<'EOF'
Usage: ./deploy/deploy.sh [--hot] [-fresh] [--ecs-workers]

  (default)  Fast image deploy: rebuild only service image(s) whose source/shared
             dependency or image recipe fingerprint changed, then run compose up
             without forcing unchanged containers to restart.

  --hot      For source-only changes in already-running services, publish that .NET
             project with a cached NuGet folder, copy the publish output into the
             running container, and restart only that service. Dockerfile/compose/tool
             changes still fall back to image rebuilds.

  -fresh     Rebuild all service images with --pull --no-cache and force recreate.

  --ecs-workers
             EC2 production mode: run the core self-hosted stack locally via
             Docker Compose, scale local workers to zero, create/update ECS
             resources, push worker images to ECR, and create/update ECS worker
             services.

Environment:
  NIGHTMARE_DEPLOY_MODE=image|hot
  NIGHTMARE_GIT_PULL=1
  NIGHTMARE_SKIP_INSTALL=1
  NIGHTMARE_DEPLOY_FRESH=1
  NIGHTMARE_NO_CACHE=1
  NIGHTMARE_PULL_IMAGES=1
  NIGHTMARE_FORCE_RECREATE=1
  NIGHTMARE_ECS_WORKERS=1
  NIGHTMARE_ECS_REPLACE_WORKERS=1
  NIGHTMARE_SKIP_BLAZOR_ASSET_VERIFY=1
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
export NIGHTMARE_DEPLOY_MODE
export NIGHTMARE_ECS_WORKERS
export NIGHTMARE_ECS_REPLACE_WORKERS
if [[ "$NIGHTMARE_ECS_WORKERS" == "1" ]]; then
  export NIGHTMARE_GIT_PULL="${NIGHTMARE_GIT_PULL:-1}"
fi

# shellcheck source=deploy/lib-nightmare-compose.sh
source "$DEPLOY_DIR/lib-nightmare-compose.sh"
# shellcheck source=deploy/lib-install-deps.sh
source "$DEPLOY_DIR/lib-install-deps.sh"

nightmare_verify_command_center_blazor_static_assets() {
  if [[ "${NIGHTMARE_SKIP_BLAZOR_ASSET_VERIFY:-0}" == "1" ]]; then
    echo "Skipping Blazor static asset verification because NIGHTMARE_SKIP_BLAZOR_ASSET_VERIFY=1."
    return 0
  fi

  echo ""
  echo "Verifying Command Center Blazor static assets..."

  local cid
  cid="$(compose ps -q command-center | tail -n 1 || true)"
  if [[ -z "$cid" ]]; then
    echo "ERROR: command-center container was not found after deployment." >&2
    echo "Run: docker compose -f deploy/docker-compose.yml ps" >&2
    exit 1
  fi

  if ! nightmare_docker inspect "$cid" >/dev/null 2>&1; then
    echo "ERROR: command-center container id '$cid' is not inspectable." >&2
    exit 1
  fi

  local running
  running="$(nightmare_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
  if [[ "$running" != "true" ]]; then
    echo "ERROR: command-center container is not running." >&2
    compose ps command-center >&2 || true
    compose logs --tail=150 command-center >&2 || true
    exit 1
  fi

  if ! nightmare_docker exec "$cid" sh -lc 'test -s /app/wwwroot/_framework/blazor.web.js'; then
    echo "ERROR: command-center container is missing /app/wwwroot/_framework/blazor.web.js." >&2
    echo "This usually means the running container is stale or a hot deploy copied an incomplete publish output." >&2
    echo "Fix with: ./deploy/deploy.sh -fresh" >&2
    nightmare_docker exec "$cid" sh -lc 'echo "Contents of /app/wwwroot:"; ls -la /app/wwwroot 2>/dev/null || true; echo "Contents of /app/wwwroot/_framework:"; ls -la /app/wwwroot/_framework 2>/dev/null || true' >&2 || true
    exit 1
  fi

  local tmp
  tmp="$(mktemp)"
  if ! curl -fsS --max-time 20 "http://127.0.0.1:8080/_framework/blazor.web.js" -o "$tmp"; then
    rm -f "$tmp"
    echo "ERROR: http://127.0.0.1:8080/_framework/blazor.web.js did not return HTTP 200 from the host." >&2
    echo "The file exists inside the container, but the app is not serving it correctly." >&2
    echo "Check Program.cs static asset ordering and command-center logs." >&2
    compose logs --tail=150 command-center >&2 || true
    exit 1
  fi

  if [[ ! -s "$tmp" ]]; then
    rm -f "$tmp"
    echo "ERROR: /_framework/blazor.web.js returned an empty response." >&2
    exit 1
  fi

  if grep -qiE '<!DOCTYPE html|<html|404[: ]|Not Found' "$tmp"; then
    echo "ERROR: /_framework/blazor.web.js returned HTML or 404-like content instead of JavaScript." >&2
    echo "First 40 lines returned:" >&2
    head -40 "$tmp" >&2 || true
    rm -f "$tmp"
    exit 1
  fi

  rm -f "$tmp"
  echo "Blazor static asset verification passed: /_framework/blazor.web.js is present and served."
}

if [[ "$NIGHTMARE_ECS_WORKERS" == "1" && -f "$DEPLOY_DIR/aws/.env" ]]; then
  # shellcheck source=/dev/null
  set -a
  . "$DEPLOY_DIR/aws/.env"
  set +a
fi

nightmare_ensure_runtime_dependencies
if [[ "$NIGHTMARE_ECS_WORKERS" == "1" ]]; then
  nightmare_ensure_curl
  nightmare_ensure_git || true
  nightmare_ensure_python3
  nightmare_ensure_aws_cli
fi

nightmare_maybe_git_pull "$ROOT"
nightmare_export_build_stamp "$ROOT"
nightmare_export_component_versions "$ROOT"
if [[ "$NIGHTMARE_ECS_WORKERS" == "1" && "${NIGHTMARE_ECS_USE_MUTABLE_TAG:-0}" != "1" ]]; then
  ecs_source_stamp="$BUILD_SOURCE_STAMP"
  if [[ -d "$ROOT/.git" ]] && ! git -C "$ROOT" diff --quiet HEAD -- 2>/dev/null; then
    dirty_hash="$(git -C "$ROOT" diff --binary HEAD -- 2>/dev/null | sha256sum | awk '{print $1}' | cut -c1-16)"
    ecs_source_stamp="${ecs_source_stamp}-${dirty_hash}"
  fi
  ecs_image_tag="$(printf '%s' "$ecs_source_stamp" | tr -c 'A-Za-z0-9_.-' '-' | cut -c1-120)"
  export IMAGE_TAG="${ecs_image_tag:-nightmare-build}"
  echo "ECS IMAGE_TAG=${IMAGE_TAG}"
fi
nightmare_decide_incremental_deploy

if [[ "$NIGHTMARE_ECS_WORKERS" == "1" ]]; then
  echo "Applying core stack from: $ROOT (local workers scaled to zero; ECS workers enabled)"
else
  echo "Applying stack from: $ROOT"
fi
nightmare_compose_full_redeploy

nightmare_verify_command_center_blazor_static_assets

if [[ "$NIGHTMARE_ECS_WORKERS" == "1" ]]; then
  echo ""
  echo "Bootstrapping ECS resources from this EC2 instance..."
  bash "$DEPLOY_DIR/aws/bootstrap-ecs-from-ec2.sh"

  # shellcheck source=/dev/null
  set -a
  [[ ! -f "$DEPLOY_DIR/aws/.env" ]] || . "$DEPLOY_DIR/aws/.env"
  . "$DEPLOY_DIR/aws/.env.generated"
  set +a

  # shellcheck disable=SC2206
  all_changed=( ${NIGHTMARE_CHANGED_SERVICES:-} ${NIGHTMARE_IMAGE_REBUILD_SERVICES:-} )
  changed_workers=()
  for w in worker-spider worker-enum worker-portscan worker-highvalue worker-techid; do
    if [[ "${NIGHTMARE_DEPLOY_FRESH:-0}" == "1" ]]; then
      changed_workers+=("$w")
    elif [[ " ${all_changed[*]:-} " == *" $w "* ]]; then
      changed_workers+=("$w")
    fi
  done

  if [[ ${#changed_workers[@]} -eq 0 ]]; then
    echo "No worker services changed. Skipping ECR build and ECS task replacement."
  else
    echo "Ensuring ECR repositories..."
    bash "$DEPLOY_DIR/aws/create-ecr-repos.sh"

    echo "Building and pushing ECR images for: ${changed_workers[*]}"
    bash "$DEPLOY_DIR/aws/build-push-ecr.sh" "${changed_workers[@]}"

    if [[ "$NIGHTMARE_ECS_REPLACE_WORKERS" == "1" ]]; then
      echo "Replacing existing ECS worker tasks for: ${changed_workers[*]}"
      bash "$DEPLOY_DIR/aws/replace-ecs-worker-tasks.sh" "${changed_workers[@]}"
      bash "$DEPLOY_DIR/aws/record-cloud-usage-sample.sh" || true
      export UPDATE_DESIRED_COUNTS=true
    fi
  fi

  if [[ ${#changed_workers[@]} -gt 0 ]]; then
    echo "Creating/updating ECS worker services for: ${changed_workers[*]}"
    bash "$DEPLOY_DIR/aws/deploy-ecs-services.sh" "${changed_workers[@]}"

    echo "Applying one autoscale pass..."
    bash "$DEPLOY_DIR/aws/autoscale-ecs-workers.sh" || true
  else
    echo "No ECS worker services need updating."
  fi
fi

echo ""
echo "Nightmare v2 is running."
echo "  Command Center:  http://localhost:8080/  (use host public IP on EC2)"
echo "  Blazor runtime:  http://localhost:8080/_framework/blazor.web.js"
echo "  RabbitMQ admin:  http://localhost:15672/  (user/pass: nightmare / nightmare)"
echo "  Postgres:        localhost:5432  db=nightmare_v2 (+ file blobs db nightmare_v2_files)  user=nightmare"
echo ""
echo "Subdomain enumeration tools are installed into the worker images:"
echo "  docker compose -f deploy/docker-compose.yml run --rm --entrypoint sh worker-enum -c 'command -v subfinder && command -v amass && test -s /opt/nightmare/wordlists/subdomains.txt'"
echo ""
echo "Useful commands (from $ROOT):"
echo "  ./deploy/deploy.sh --hot                  # source-only hot-swap into running containers"
echo "  ./deploy/deploy.sh -fresh                 # full image rebuild; use this if Blazor static assets are stale/missing"
echo "  ./deploy/prebuild-cache.sh                # warm image/NuGet/Go caches before debugging"
echo "  ./deploy/smoke-test.sh                    # verify health, Blazor static assets, and dependency diagnostics"
echo "  ./deploy/logs.sh --errors                 # show recent error-like log lines across services"
echo "  ./deploy/logs.sh --follow worker-spider   # follow one service"
echo "  docker compose -f deploy/docker-compose.yml down"
if [[ "$NIGHTMARE_ECS_WORKERS" == "1" ]]; then
  echo "  deploy/aws/autoscale-ecs-workers.sh        # run from cron/EventBridge for continuous ECS worker scaling"
  echo "  ./deploy/deploy.sh --ecs-workers          # pull latest GitHub code, replace ECS worker tasks"
  echo "  CONFIRM_DESTROY_ECS_WORKERS=yes bash deploy/aws/destroy-ecs-services.sh workers"
fi
echo "(or docker-compose -f deploy/docker-compose.yml ... if you use V1)"
echo ""