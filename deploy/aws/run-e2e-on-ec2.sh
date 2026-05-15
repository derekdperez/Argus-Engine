#!/usr/bin/env bash
# Deploy the requested Git ref to the long-lived E2E EC2 host, reset its DBs,
# run the E2E suite on that host, and return the SSM command result.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

if [[ -f "${script_dir}/.env" ]]; then
  set -a
  # shellcheck source=/dev/null
  . "${script_dir}/.env"
  set +a
fi

: "${AWS_REGION:?Set AWS_REGION}"
: "${E2E_GIT_REF:=${GITHUB_SHA:-main}}"
: "${E2E_REPOSITORY_URL:=${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-derekdperez/argusV2}.git}"
: "${E2E_COMMAND_TIMEOUT_SECONDS:=5400}"
: "${E2E_COMMAND_CENTER_URL:=http://127.0.0.1:8080}"

quote() {
  python3 -c 'import shlex, sys; print(shlex.quote(sys.argv[1]))' "$1"
}

echo "Ensuring E2E EC2 test server exists..."
instance_id="$("${script_dir}/provision-e2e-test-server.sh" | tail -n 1)"
if [[ -z "$instance_id" || "$instance_id" == "None" ]]; then
  echo "E2E server provisioning did not return an instance id." >&2
  exit 1
fi

echo "Waiting for EC2 instance ${instance_id} to be running and status-ok..."
aws ec2 wait instance-running --region "$AWS_REGION" --instance-ids "$instance_id"
aws ec2 wait instance-status-ok --region "$AWS_REGION" --instance-ids "$instance_id"

echo "Waiting for SSM connectivity on ${instance_id}..."
ssm_ready=0
for _ in $(seq 1 60); do
  ping_status="$(aws ssm describe-instance-information \
    --region "$AWS_REGION" \
    --filters "Key=InstanceIds,Values=${instance_id}" \
    --query 'InstanceInformationList[0].PingStatus' \
    --output text 2>/dev/null || true)"

  if [[ "$ping_status" == "Online" ]]; then
    ssm_ready=1
    break
  fi

  sleep 10
done

if [[ "$ssm_ready" != "1" ]]; then
  echo "E2E instance ${instance_id} did not become SSM Online. Check its instance profile includes AmazonSSMManagedInstanceCore." >&2
  exit 1
fi

repo_q="$(quote "$E2E_REPOSITORY_URL")"
ref_q="$(quote "$E2E_GIT_REF")"
base_url_q="$(quote "$E2E_COMMAND_CENTER_URL")"
snapshot_q="$(quote "${ARGUS_E2E_DB_SNAPSHOT_SQL:-}")"

remote_script_file="$(mktemp)"
cat >"$remote_script_file" <<EOF
set -euo pipefail
if [[ ! -d /opt/argus/.git ]]; then
  sudo rm -rf /opt/argus
  sudo git clone ${repo_q} /opt/argus
  sudo chown -R ubuntu:ubuntu /opt/argus
fi
cd /opt/argus
git remote set-url origin ${repo_q}
git fetch --prune origin
git checkout --detach --force ${ref_q}
chmod +x deploy/deploy.sh src/tests/e2e/*.sh scripts/run-tests.sh test.sh || true
export argus_GIT_PULL=0
export COMPOSE_BAKE=false
./deploy/deploy.sh --hot
export ARGUS_BASE_URL=${base_url_q}
export ARGUS_E2E_DB_SNAPSHOT_SQL=${snapshot_q}
./src/tests/e2e/run-e2e-suite.sh
EOF

remote_script_b64="$(base64 <"$remote_script_file" | tr -d '\n')"
rm -f "$remote_script_file"
remote_command="printf '%s' '${remote_script_b64}' | base64 -d >/tmp/argus-e2e-run.sh && chmod +x /tmp/argus-e2e-run.sh && sudo -iu ubuntu bash /tmp/argus-e2e-run.sh"

parameters_file="$(mktemp)"
REMOTE_COMMAND="$remote_command" E2E_COMMAND_TIMEOUT_SECONDS="$E2E_COMMAND_TIMEOUT_SECONDS" python3 - >"$parameters_file" <<'PY'
import json
import os

print(json.dumps({
    "commands": [os.environ["REMOTE_COMMAND"]],
    "executionTimeout": [os.environ["E2E_COMMAND_TIMEOUT_SECONDS"]],
}))
PY

echo "Starting remote E2E deploy/test command on ${instance_id}..."
command_id="$(aws ssm send-command \
  --region "$AWS_REGION" \
  --instance-ids "$instance_id" \
  --document-name AWS-RunShellScript \
  --comment "Argus E2E deploy and test ${E2E_GIT_REF}" \
  --parameters "file://${parameters_file}" \
  --query 'Command.CommandId' \
  --output text)"
rm -f "$parameters_file"

echo "SSM command id: ${command_id}"

terminal_status=""
for _ in $(seq 1 $((E2E_COMMAND_TIMEOUT_SECONDS / 10 + 6))); do
  status="$(aws ssm get-command-invocation \
    --region "$AWS_REGION" \
    --command-id "$command_id" \
    --instance-id "$instance_id" \
    --query 'Status' \
    --output text 2>/dev/null || true)"

  case "$status" in
    Success|Cancelled|TimedOut|Failed|Cancelling)
      terminal_status="$status"
      break
      ;;
  esac

  sleep 10
done

if [[ -z "$terminal_status" ]]; then
  terminal_status="TimedOut"
fi

invocation_json="$(mktemp)"
aws ssm get-command-invocation \
  --region "$AWS_REGION" \
  --command-id "$command_id" \
  --instance-id "$instance_id" \
  --output json >"$invocation_json"

python3 - "$invocation_json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    doc = json.load(handle)

print("Remote E2E status:", doc.get("Status"))
stdout = (doc.get("StandardOutputContent") or "").strip()
stderr = (doc.get("StandardErrorContent") or "").strip()
if stdout:
    print("\n--- remote stdout ---")
    print(stdout[-12000:])
if stderr:
    print("\n--- remote stderr ---", file=sys.stderr)
    print(stderr[-12000:], file=sys.stderr)
PY

rm -f "$invocation_json"

if [[ "$terminal_status" != "Success" ]]; then
  echo "Remote E2E deploy/test failed with SSM status ${terminal_status}." >&2
  exit 1
fi

echo "Remote E2E deploy/test passed on ${instance_id}."
