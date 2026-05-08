#!/usr/bin/env bash
# One-command local / EC2 deploy for the full argus v2 .NET stack (Docker Compose).
# Re-runnable: safe to run again.
#
# Default (incremental): fingerprints each app service and rebuilds only the service images whose
# own source/shared dependencies changed. It does not pull base images or force-recreate unchanged
# containers on every deploy. Use -fresh to always rebuild all images with --no-cache.
#
# Dependencies (Linux): Docker Engine + Compose are installed automatically via get.docker.com and
# your distro package manager when missing (requires sudo). Set argus_SKIP_INSTALL=1 to only verify.
# macOS / Windows: install Docker Desktop yourself, then re-run.
#
# Requires either (after bootstrap):
#   - Docker Compose V2:  "docker compose version" works, or
#   - Docker Compose V1:  standalone "docker-compose" on PATH.
#
# Optional environment:
#   argus_DEPLOY_MODE=image|hot  image=normal cached image deploy (default); hot=publish/copy/restart changed running services.
#   argus_GIT_PULL=1   Run git pull --ff-only in the repo before building (remote must be ff-only). Defaults to 1 for --ecs-workers.
#   argus_NO_CACHE=1      docker compose build --no-cache (also implied by -fresh)
#   argus_PULL_IMAGES=1    docker compose build --pull. Defaults to 0 for fast deploys.
#   argus_FORCE_RECREATE=1 Force recreate containers. Defaults to 0 for fast deploys.
#   argus_SKIP_INSTALL=1   Do not install Docker / curl / git; fail if docker or compose is missing.
#   argus_DEPLOY_FRESH=1   Same as passing -fresh on the command line.
#   argus_ECS_REPLACE_WORKERS=1  In --ecs-workers mode, stop existing ECS worker tasks before recreating them.
#   argus_SKIP_BLAZOR_ASSET_VERIFY=1  Skip post-deploy verification of /_framework/blazor.web.js.
#   argus_BUILD_TIMEOUT_MIN=0  Max minutes for a compose build invocation; 0 disables timeout.
#   argus_BUILD_SEQUENTIAL=0   Build selected services one-by-one for clearer progress/isolation.
#   argus_BUILD_PROGRESS=auto  Build progress style: auto|plain|tty.
#   SUBFINDER_VERSION=...  Pinned vendored subfinder release version.
#   AMASS_VERSION=...      Pinned vendored amass release version.
#   COMPOSE_BAKE=true|false    Multi-service compose builds may use "bake"; scripts default to false for stability.
#   argus_ECS_WORKERS=1     Deploy core stack locally on EC2 and deploy workers as ECS services.
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

argus_DEPLOY_FRESH="${argus_DEPLOY_FRESH:-0}"
argus_DEPLOY_MODE="${argus_DEPLOY_MODE:-image}"
argus_ECS_WORKERS="${argus_ECS_WORKERS:-0}"
argus_ECS_REPLACE_WORKERS="${argus_ECS_REPLACE_WORKERS:-1}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ecs-workers)
      argus_ECS_WORKERS=1
      argus_DEPLOY_MODE=image
      shift
      ;;
    -fresh | --fresh)
      argus_DEPLOY_FRESH=1
      argus_DEPLOY_MODE=image
      shift
      ;;
    --hot | -hot)
      argus_DEPLOY_MODE=hot
      shift
      ;;
    --image | -image)
      argus_DEPLOY_MODE=image
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
  argus_DEPLOY_MODE=image|hot
  argus_GIT_PULL=1
  argus_SKIP_INSTALL=1
  argus_DEPLOY_FRESH=1
  argus_NO_CACHE=1
  argus_PULL_IMAGES=1
  argus_FORCE_RECREATE=1
  argus_ECS_WORKERS=1
  argus_ECS_REPLACE_WORKERS=1
  argus_SKIP_BLAZOR_ASSET_VERIFY=1
  argus_BUILD_TIMEOUT_MIN=0
  argus_BUILD_SEQUENTIAL=0
  argus_BUILD_PROGRESS=auto
EOF
      exit 0
      ;;
    *)
      echo "Unknown argument: $1 (use -h for help)" >&2
      exit 1
      ;;
  esac
done
export argus_DEPLOY_FRESH
export argus_DEPLOY_MODE
export argus_ECS_WORKERS
export argus_ECS_REPLACE_WORKERS
if [[ "$argus_ECS_WORKERS" == "1" ]]; then
  export argus_GIT_PULL="${argus_GIT_PULL:-1}"
fi

# shellcheck source=deploy/lib-argus-compose.sh
source "$DEPLOY_DIR/lib-argus-compose.sh"
# shellcheck source=deploy/lib-install-deps.sh
source "$DEPLOY_DIR/lib-install-deps.sh"

argus_verify_command_center_blazor_static_assets() {
  if [[ "${argus_SKIP_BLAZOR_ASSET_VERIFY:-0}" == "1" ]]; then
    echo "Skipping Blazor static asset verification because argus_SKIP_BLAZOR_ASSET_VERIFY=1."
    return 0
  fi

  echo ""
  echo "Verifying Command Center Blazor static assets..."

  local cid
  cid="$(compose ps -q command-center-web | tail -n 1 || true)"
  if [[ -z "$cid" ]]; then
    echo "ERROR: command-center-web container was not found after deployment." >&2
    echo "Run: docker compose -f deploy/docker-compose.yml ps" >&2
    exit 1
  fi

  if ! argus_docker inspect "$cid" >/dev/null 2>&1; then
    echo "ERROR: command-center-web container id '$cid' is not inspectable." >&2
    exit 1
  fi

  local running
  running="$(argus_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
  if [[ "$running" != "true" ]]; then
    echo "ERROR: command-center-web container is not running." >&2
    compose ps command-center-web >&2 || true
    compose logs --tail=150 command-center-web >&2 || true
    exit 1
  fi

  if ! argus_docker exec "$cid" sh -lc 'test -s /app/wwwroot/_framework/blazor.web.js'; then
    echo "command-center-web is missing /app/wwwroot/_framework/blazor.web.js; attempting automatic recovery..." >&2
    if ! argus_recover_command_center_blazor_script "$cid"; then
      echo "ERROR: automatic recovery failed for /app/wwwroot/_framework/blazor.web.js." >&2
      argus_docker exec "$cid" sh -lc 'echo "Contents of /app/wwwroot:"; ls -la /app/wwwroot 2>/dev/null || true; echo "Contents of /app/wwwroot/_framework:"; ls -la /app/wwwroot/_framework 2>/dev/null || true' >&2 || true
      exit 1
    fi
    cid="$(compose ps -q command-center-web | tail -n 1 || true)"
    if [[ -z "$cid" ]] || ! argus_docker exec "$cid" sh -lc 'test -s /app/wwwroot/_framework/blazor.web.js'; then
      echo "ERROR: command-center-web still does not have /app/wwwroot/_framework/blazor.web.js after recovery." >&2
      exit 1
    fi
  fi

  local tmp
  tmp="$(mktemp)"
  if ! curl -fsS --max-time 20 "http://127.0.0.1:8080/_framework/blazor.web.js" -o "$tmp"; then
    echo "command-center-web failed to serve /_framework/blazor.web.js; attempting automatic recovery..." >&2
    if ! argus_recover_command_center_blazor_script "$cid"; then
      rm -f "$tmp"
      echo "ERROR: automatic recovery failed and /_framework/blazor.web.js still returns non-200." >&2
      compose logs --tail=150 command-center-web >&2 || true
      exit 1
    fi
    cid="$(compose ps -q command-center-web | tail -n 1 || true)"
    rm -f "$tmp"
    if ! curl -fsS --max-time 20 "http://127.0.0.1:8080/_framework/blazor.web.js" -o "$tmp"; then
      rm -f "$tmp"
      echo "ERROR: /_framework/blazor.web.js still does not return HTTP 200 after recovery." >&2
      compose logs --tail=150 command-center-web >&2 || true
      exit 1
    fi
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

argus_recover_command_center_blazor_script() {
  local cid="$1"

  echo "Recovery step 1/2: try runtime copy from ASP.NET shared framework..." >&2
  if argus_docker exec "$cid" sh -lc '
    set -eu
    mkdir -p /app/wwwroot/_framework
    src=""
    for p in /usr/share/dotnet/shared/Microsoft.AspNetCore.App/*/blazor.web.js \
             /usr/share/dotnet/shared/Microsoft.AspNetCore.App/*/wwwroot/_framework/blazor.web.js; do
      if [ -s "$p" ]; then
        src="$p"
        break
      fi
    done
    if [ -n "$src" ]; then
      cp "$src" /app/wwwroot/_framework/blazor.web.js
      test -s /app/wwwroot/_framework/blazor.web.js
      exit 0
    fi
    exit 1
  '; then
    return 0
  fi

  echo "Recovery step 2/2: publish command-center-web and hot-copy fresh output..." >&2
  if ! argus_publish_service_for_hot_swap "command-center-web"; then
    echo "command-center-web hot publish failed during recovery." >&2
    return 1
  fi

  local out_abs="$ROOT/deploy/.hot-publish/command-center-web"
  if [[ ! -s "$out_abs/wwwroot/_framework/blazor.web.js" ]]; then
    echo "Hot publish output is missing blazor.web.js; cannot recover safely." >&2
    return 1
  fi

  argus_hot_copy_publish_output_to_container "command-center-web" "$cid" "$out_abs"
  compose restart command-center-web
  return 0
}

if [[ "$argus_ECS_WORKERS" == "1" && -f "$DEPLOY_DIR/aws/.env" ]]; then
  # shellcheck source=/dev/null
  set -a
  . "$DEPLOY_DIR/aws/.env"
  set +a
fi

argus_ensure_runtime_dependencies
if [[ "$argus_ECS_WORKERS" == "1" ]]; then
  argus_ensure_curl
  argus_ensure_git || true
  argus_ensure_python3
  argus_ensure_aws_cli
fi

argus_maybe_git_pull "$ROOT"
argus_export_build_stamp "$ROOT"
argus_export_component_versions "$ROOT"
if [[ "$argus_ECS_WORKERS" == "1" && "${argus_ECS_USE_MUTABLE_TAG:-0}" != "1" ]]; then
  ecs_source_stamp="$BUILD_SOURCE_STAMP"
  if [[ -d "$ROOT/.git" ]] && ! git -C "$ROOT" diff --quiet HEAD -- 2>/dev/null; then
    dirty_hash="$(git -C "$ROOT" diff --binary HEAD -- 2>/dev/null | sha256sum | awk '{print $1}' | cut -c1-16)"
    ecs_source_stamp="${ecs_source_stamp}-${dirty_hash}"
  fi
  ecs_image_tag="$(printf '%s' "$ecs_source_stamp" | tr -c 'A-Za-z0-9_.-' '-' | cut -c1-120)"
  export IMAGE_TAG="${ecs_image_tag:-argus-build}"
  echo "ECS IMAGE_TAG=${IMAGE_TAG}"
fi
argus_decide_incremental_deploy

if [[ "$argus_ECS_WORKERS" == "1" ]]; then
  echo "Applying core stack from: $ROOT (local workers scaled to zero; ECS workers enabled)"
else
  echo "Applying stack from: $ROOT"
fi
argus_compose_full_redeploy

# argus_verify_command_center_blazor_static_assets

if [[ "$argus_ECS_WORKERS" == "1" ]]; then
  echo ""
  echo "Bootstrapping ECS resources from this EC2 instance..."
  bash "$DEPLOY_DIR/aws/bootstrap-ecs-from-ec2.sh"

  # shellcheck source=/dev/null
  set -a
  [[ ! -f "$DEPLOY_DIR/aws/.env" ]] || . "$DEPLOY_DIR/aws/.env"
  . "$DEPLOY_DIR/aws/.env.generated"
  set +a

  # shellcheck disable=SC2206
  all_changed=( ${argus_CHANGED_SERVICES:-} ${argus_IMAGE_REBUILD_SERVICES:-} )
  changed_workers=()
  for w in worker-spider worker-enum worker-portscan worker-highvalue worker-techid; do
    if [[ "${argus_DEPLOY_FRESH:-0}" == "1" ]]; then
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

    if [[ "$argus_ECS_REPLACE_WORKERS" == "1" ]]; then
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
echo "argus v2 is running."
echo "  Command Center:  http://localhost:8080/  (use host public IP on EC2)"
echo "  # Blazor runtime:  http://localhost:8080/_framework/blazor.web.js (or fingerprinted equivalent)"
echo "  RabbitMQ admin:  http://localhost:15672/  (user/pass: argus / argus)"
echo "  Postgres:        localhost:5432  db=argus_engine (+ file blobs db argus_engine_files)  user=argus"
echo ""
echo "Subdomain enumeration tools are installed into the worker images:"
echo "  docker compose -f deploy/docker-compose.yml run --rm --entrypoint sh worker-enum -c 'command -v subfinder && command -v amass && test -s /opt/argus/wordlists/subdomains.txt'"
echo ""
echo "Useful commands (from $ROOT):"
echo "  ./deploy/deploy.sh --hot                  # source-only hot-swap into running containers"
echo "  ./deploy/deploy.sh -fresh                 # full image rebuild; use this if Blazor static assets are stale/missing"
echo "  ./deploy/prebuild-cache.sh                # warm image/NuGet/Go caches before debugging"
echo "  ./deploy/smoke-test.sh                    # verify health, Blazor static assets, and dependency diagnostics"
echo "  ./deploy/logs.sh --errors                 # show recent error-like log lines across services"
echo "  ./deploy/logs.sh --follow worker-spider   # follow one service"
echo "  docker compose -f deploy/docker-compose.yml down"
if [[ "$argus_ECS_WORKERS" == "1" ]]; then
  echo "  deploy/aws/autoscale-ecs-workers.sh        # run from cron/EventBridge for continuous ECS worker scaling"
  echo "  ./deploy/deploy.sh --ecs-workers          # pull latest GitHub code, replace ECS worker tasks"
  echo "  CONFIRM_DESTROY_ECS_WORKERS=yes bash deploy/aws/destroy-ecs-services.sh workers"
fi
echo "(or docker-compose -f deploy/docker-compose.yml ... if you use V1)"
echo ""

