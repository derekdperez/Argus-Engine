#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../cloud-common.sh
source "$SCRIPT_DIR/../cloud-common.sh"

argus_warn_if_sudo

printf 'Argus repo root: %s\n' "$ARGUS_REPO_ROOT"
printf 'Azure env file: %s\n' "$(argus_azure_env_file)"
printf 'Azure service env file: %s\n' "$(argus_azure_service_env_file)"
printf '\n'

for cmd in az docker; do
  if command -v "$cmd" >/dev/null 2>&1; then
    printf 'OK: %s -> %s\n' "$cmd" "$(command -v "$cmd")"
  else
    printf 'MISSING: %s\n' "$cmd"
  fi
done

if command -v az >/dev/null 2>&1; then
  if az account show >/dev/null 2>&1; then
    printf 'OK: Azure CLI logged in as subscription:\n'
    az account show -o table
  else
    printf 'MISSING: Azure CLI login. Run: az login --use-device-code\n'
  fi
fi

if command -v docker >/dev/null 2>&1; then
  if docker ps >/dev/null 2>&1; then
    printf 'OK: Docker daemon reachable.\n'
  else
    printf 'MISSING: Docker daemon not reachable by this user. Try adding ec2-user to the docker group or run Docker commands with sudo.\n'
  fi
fi

if [[ -f "$ARGUS_REPO_ROOT/deploy/azure/.env" ]]; then
  printf '\nCurrent deploy/azure/.env keys:\n'
  sed -E 's/(PASSWORD|SECRET|KEY|TOKEN)([^=]*)=.*/\1\2=<redacted>/Ig' "$ARGUS_REPO_ROOT/deploy/azure/.env"
else
  printf '\nMissing deploy/azure/.env; it will be created by create-containerapps-resources.sh or build-push-acr.sh.\n'
fi
