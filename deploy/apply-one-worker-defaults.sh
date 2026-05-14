#!/usr/bin/env bash
set -euo pipefail

# Idempotently normalize Argus deployment defaults so a fresh deploy starts
# exactly one container/task/instance for each worker type:
#   worker-spider
#   worker-http-requester
#   worker-enum
#   worker-portscan
#   worker-highvalue
#   worker-techid
#
# Run from the repository root:
#   bash deploy/apply-one-worker-defaults.sh

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

log() { printf '[ARGUS] %s\n' "$*"; }
warn() { printf '[ARGUS] WARN: %s\n' "$*" >&2; }

backup_once() {
  local file="$1"
  [[ -f "$file" ]] || return 0
  [[ -f "$file.one-worker-defaults.bak" ]] || cp "$file" "$file.one-worker-defaults.bak"
}

replace_in_file() {
  local file="$1"
  local pattern="$2"
  local replacement="$3"
  [[ -f "$file" ]] || return 0
  perl -0pi -e "s/${pattern}/${replacement}/g" "$file"
}

patch_compose() {
  local file="deploy/docker-compose.yml"
  [[ -f "$file" ]] || { warn "$file not found; skipping local Compose defaults"; return 0; }
  backup_once "$file"

  # Any ARGUS_WORKER_*_REPLICAS default should start at 1.
  perl -0pi -e 's/\$\{(ARGUS_WORKER_[A-Z0-9_]+_REPLICAS):-[0-9]+\}/\${$1:-1}/g' "$file"

  log "Updated $file worker replica defaults to 1"
}

patch_lib_compose() {
  local file="deploy/lib-argus-compose.sh"
  [[ -f "$file" ]] || { warn "$file not found; skipping deploy scaling helper"; return 0; }
  backup_once "$file"

  # The deploy helper explicitly scales spider/enum to 10 because plain Docker Compose
  # may ignore deploy.replicas. Keep the explicit scale, but default it to 1.
  replace_in_file "$file" 'argus_ENUM_REPLICAS:-10' 'argus_ENUM_REPLICAS:-1'
  replace_in_file "$file" 'argus_SPIDER_REPLICAS:-10' 'argus_SPIDER_REPLICAS:-1'

  # Support optional explicit replica env vars for the rest of the worker classes.
  # If the upstream helper already has them, leave it alone.
  if ! grep -q 'argus_HTTP_REQUESTER_REPLICAS' "$file"; then
    python3 - "$file" <<'PY'
from pathlib import Path
import sys

p = Path(sys.argv[1])
text = p.read_text()

old = """args+=(
    --scale "worker-enum=${argus_ENUM_REPLICAS:-1}"
    --scale "worker-spider=${argus_SPIDER_REPLICAS:-1}"
    )"""
new = """args+=(
    --scale "worker-enum=${argus_ENUM_REPLICAS:-1}"
    --scale "worker-spider=${argus_SPIDER_REPLICAS:-1}"
    --scale "worker-portscan=${argus_PORTSCAN_REPLICAS:-1}"
    --scale "worker-highvalue=${argus_HIGHVALUE_REPLICAS:-1}"
    --scale "worker-techid=${argus_TECHID_REPLICAS:-1}"
    --scale "worker-http-requester=${argus_HTTP_REQUESTER_REPLICAS:-1}"
    )"""
if old in text:
    text = text.replace(old, new, 1)
else:
    # Handles the minified one-line form in older generated copies.
    text = text.replace(
        """args+=( --scale "worker-enum=${argus_ENUM_REPLICAS:-1}" --scale "worker-spider=${argus_SPIDER_REPLICAS:-1}" ) fi compose "${args[@]}" """.strip(),
        """args+=( --scale "worker-enum=${argus_ENUM_REPLICAS:-1}" --scale "worker-spider=${argus_SPIDER_REPLICAS:-1}" --scale "worker-portscan=${argus_PORTSCAN_REPLICAS:-1}" --scale "worker-highvalue=${argus_HIGHVALUE_REPLICAS:-1}" --scale "worker-techid=${argus_TECHID_REPLICAS:-1}" --scale "worker-http-requester=${argus_HTTP_REQUESTER_REPLICAS:-1}" ) fi compose "${args[@]}" """.strip(),
        1,
    )

p.write_text(text)
PY
  fi

  log "Updated $file explicit local deploy scale defaults to 1"
}

patch_ec2_worker_scale() {
  local file="deploy/apply-ec2-worker-scale.sh"
  [[ -f "$file" ]] || { warn "$file not found; skipping EC2 worker scale helper"; return 0; }
  backup_once "$file"

  replace_in_file "$file" 'EC2_WORKER_SPIDER:-[0-9]+' 'EC2_WORKER_SPIDER:-1'
  replace_in_file "$file" 'EC2_WORKER_ENUM:-[0-9]+' 'EC2_WORKER_ENUM:-1'
  replace_in_file "$file" 'EC2_WORKER_PORTSCAN:-[0-9]+' 'EC2_WORKER_PORTSCAN:-1'
  replace_in_file "$file" 'EC2_WORKER_HIGHVALUE:-[0-9]+' 'EC2_WORKER_HIGHVALUE:-1'
  replace_in_file "$file" 'EC2_WORKER_TECHID:-[0-9]+' 'EC2_WORKER_TECHID:-1'

  log "Updated $file EC2 worker defaults to 1"
}

patch_aws_examples_and_scripts() {
  local env_file="deploy/aws/.env.example"
  if [[ -f "$env_file" ]]; then
    backup_once "$env_file"
    replace_in_file "$env_file" 'WORKER_COUNT=10' 'WORKER_COUNT=1'
    replace_in_file "$env_file" 'INSTANCE_COUNT=2' 'INSTANCE_COUNT=1'
    replace_in_file "$env_file" 'Provision 2 new EC2 instances with 10 workers each' 'Provision 1 EC2 worker instance with 1 worker of each type'
    replace_in_file "$env_file" 'Deploy and start workers once instances are running' 'Deploy and start one of each worker type once instances are running'
    log "Updated $env_file EC2 worker examples to one worker"
  fi

  local provision="deploy/aws/provision-ec2-workers.sh"
  if [[ -f "$provision" ]]; then
    backup_once "$provision"
    replace_in_file "$provision" 'Provision 2 new EC2 instances with 10 HTTP request workers each' 'Provision 1 EC2 worker instance with 1 worker of each type'
    replace_in_file "$provision" 'WORKER_COUNT Number of workers per instance \(10\)' 'WORKER_COUNT Number of workers per instance (1)'
    replace_in_file "$provision" 'INSTANCE_COUNT Number of instances to launch \(2\)' 'INSTANCE_COUNT Number of instances to launch (1)'
    replace_in_file "$provision" 'WORKER_COUNT:=10' 'WORKER_COUNT:=1'
    replace_in_file "$provision" 'INSTANCE_COUNT:=2' 'INSTANCE_COUNT:=1'
    replace_in_file "$provision" 'deploy 10 workers to each instance' 'deploy one of each worker type to each instance'
    log "Updated $provision defaults to one worker/one instance"
  fi

  local readme="deploy/aws/README.md"
  if [[ -f "$readme" ]]; then
    backup_once "$readme"
    replace_in_file "$readme" 'Provision 2 new EC2 instances with 10 workers each' 'Provision 1 EC2 worker instance with 1 worker of each type'
    replace_in_file "$readme" 'Local docker-compose deployment with 10 workers' 'Local docker-compose deployment with 1 worker of each type'
    log "Updated $readme worker-count references"
  fi
}

patch_cloud_common() {
  local file="deploy/cloud-common.sh"
  [[ -f "$file" ]] || { warn "$file not found; skipping Azure/GCP common helper"; return 0; }
  backup_once "$file"

  python3 - "$file" <<'PY'
from pathlib import Path
import re
import sys

p = Path(sys.argv[1])
text = p.read_text()

pattern = r'argus_cloud_service_default_instances\(\)\s*\{\s*case "\$1" in.*?esac\s*\}'
replacement = """argus_cloud_service_default_instances() {
  # Fresh cloud deployments should start one instance of each worker type.
  # Scale up explicitly via provider autoscaling or per-service overrides.
  echo "1"
}"""
text2, n = re.subn(pattern, replacement, text, flags=re.S)
if n == 0 and 'argus_cloud_service_default_instances()' not in text:
    text2 = text.rstrip() + "\n\n" + replacement + "\n"
p.write_text(text2)
PY

  log "Updated $file cloud worker default instances to 1"
}

patch_cloud_examples() {
  local azure="deploy/azure/.env.example"
  if [[ -f "$azure" ]]; then
    backup_once "$azure"
    replace_in_file "$azure" 'AZURE_MIN_REPLICAS_WORKER_HTTP_REQUESTER=3' 'AZURE_MIN_REPLICAS_WORKER_HTTP_REQUESTER=1'
    replace_in_file "$azure" 'AZURE_MAX_REPLICAS_WORKER_HTTP_REQUESTER=10' 'AZURE_MAX_REPLICAS_WORKER_HTTP_REQUESTER=1'
    log "Updated $azure per-service override examples"
  fi

  local gcp="deploy/gcp/.env.example"
  if [[ -f "$gcp" ]]; then
    backup_once "$gcp"
    replace_in_file "$gcp" 'GCP_WORKER_INSTANCES_WORKER_HTTP_REQUESTER=3' 'GCP_WORKER_INSTANCES_WORKER_HTTP_REQUESTER=1'
    log "Updated $gcp per-service override examples"
  fi
}

write_defaults_env() {
  local file="deploy/one-worker-defaults.env.example"
  cat >"$file" <<'EOF'
# Optional explicit one-worker defaults for local/cloud deployments.
# These values mirror the defaults applied by deploy/apply-one-worker-defaults.sh.

# deploy/lib-argus-compose.sh explicit local scale controls
argus_ENUM_REPLICAS=1
argus_SPIDER_REPLICAS=1
argus_PORTSCAN_REPLICAS=1
argus_HIGHVALUE_REPLICAS=1
argus_TECHID_REPLICAS=1
argus_HTTP_REQUESTER_REPLICAS=1

# deploy/docker-compose.yml worker replica variables
ARGUS_WORKER_SPIDER_REPLICAS=1
ARGUS_WORKER_HTTP_REQUESTER_REPLICAS=1
ARGUS_WORKER_ENUM_REPLICAS=1
ARGUS_WORKER_PORTSCAN_REPLICAS=1
ARGUS_WORKER_HIGHVALUE_REPLICAS=1
ARGUS_WORKER_TECHID_REPLICAS=1

# AWS ECS initial desired counts
ECS_DESIRED_WORKER_SPIDER=1
ECS_DESIRED_WORKER_ENUM=1
ECS_DESIRED_WORKER_PORTSCAN=1
ECS_DESIRED_WORKER_HIGHVALUE=1
ECS_DESIRED_WORKER_TECHID=1

# Manual EC2 worker machine defaults
EC2_WORKER_SPIDER=1
EC2_WORKER_ENUM=1
EC2_WORKER_PORTSCAN=1
EC2_WORKER_HIGHVALUE=1
EC2_WORKER_TECHID=1

# Azure Container Apps and Google Cloud Run Worker Pools
AZURE_MIN_REPLICAS=1
GCP_WORKER_INSTANCES=1
EOF
  log "Wrote $file"
}

patch_compose
patch_lib_compose
patch_ec2_worker_scale
patch_aws_examples_and_scripts
patch_cloud_common
patch_cloud_examples
write_defaults_env

cat <<'EOF'

One-worker deployment defaults applied.

Recommended verification:
  git diff -- deploy/docker-compose.yml deploy/lib-argus-compose.sh deploy/apply-ec2-worker-scale.sh deploy/aws deploy/cloud-common.sh deploy/azure deploy/gcp deploy/one-worker-defaults.env.example

Then redeploy locally:
  ARGUS_NO_UI=1 argus_DEPLOY_MODE=image argus_BUILD_SEQUENTIAL=1 argus_BUILD_TIMEOUT_MIN=0 bash deploy/deploy.sh --image

EOF
