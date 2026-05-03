#!/usr/bin/env bash
set -euo pipefail

expected="${1:-2.2.0}"
fail=0

check_file() {
  local file="$1"
  local pattern="$2"
  if ! grep -q "$pattern" "$file"; then
    echo "ERROR: $file does not contain expected pattern: $pattern" >&2
    fail=1
  fi
}

check_file VERSION "^${expected}$"
check_file Directory.Build.targets "<ArgusEngineDeploymentVersion>${expected}</ArgusEngineDeploymentVersion>"
check_file deploy/Dockerfile.web "ARG COMPONENT_VERSION=${expected}"
check_file deploy/Dockerfile.worker "ARG COMPONENT_VERSION=${expected}"
check_file deploy/Dockerfile.worker-enum "ARG COMPONENT_VERSION=${expected}"
check_file deploy/docker-compose.yml "ARGUS_ENGINE_VERSION:-${expected}"

if grep -R "COMPONENT_VERSION: .*2\.0\.0\|COMPONENT_VERSION=2\.0\.0\|VERSION_.*:-2\.0\.0" deploy Directory.Build.* src 2>/dev/null; then
  echo "ERROR: stale 2.0.0 deployment version defaults remain." >&2
  fail=1
fi

exit "$fail"
