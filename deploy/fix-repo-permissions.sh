#!/usr/bin/env bash
set -euo pipefail

# Repairs local filesystem ownership and permissions for an Argus Engine checkout.
# Run from the repo root:
#   bash deploy/fix-repo-permissions.sh
#
# Git tracks executable bits but not file ownership. If files were created by
# sudo unzip/copy, this script returns the working tree to the normal user.

ROOT="${1:-}"
if [[ -z "${ROOT}" ]]; then
  if command -v git >/dev/null 2>&1 && git rev-parse --show-toplevel >/dev/null 2>&1; then
    ROOT="$(git rev-parse --show-toplevel)"
  else
    ROOT="$(pwd)"
  fi
fi

ROOT="$(cd "${ROOT}" && pwd)"

if [[ ! -d "${ROOT}/.git" ]]; then
  echo "[ARGUS] Refusing to run: ${ROOT} does not look like a git repo root." >&2
  echo "[ARGUS] Pass the repo root explicitly if needed: bash deploy/fix-repo-permissions.sh /path/to/argus-engine" >&2
  exit 2
fi

# When run with sudo, repair ownership back to the original login user.
TARGET_USER="${TARGET_USER:-${SUDO_USER:-$(id -un)}}"

if ! id "${TARGET_USER}" >/dev/null 2>&1; then
  echo "[ARGUS] Unknown TARGET_USER=${TARGET_USER}" >&2
  exit 2
fi

TARGET_GROUP="${TARGET_GROUP:-$(id -gn "${TARGET_USER}")}"

echo "[ARGUS] Repo root: ${ROOT}"
echo "[ARGUS] Target owner: ${TARGET_USER}:${TARGET_GROUP}"

# chown needs sudo unless every file is already owned by the current user.
if [[ "$(id -u)" -eq 0 ]]; then
  chown -R "${TARGET_USER}:${TARGET_GROUP}" "${ROOT}"
else
  if command -v sudo >/dev/null 2>&1; then
    sudo chown -R "${TARGET_USER}:${TARGET_GROUP}" "${ROOT}"
  else
    chown -R "${TARGET_USER}:${TARGET_GROUP}" "${ROOT}"
  fi
fi

# Normalize write/read permissions. Avoid making every file executable.
find "${ROOT}" -path "${ROOT}/.git" -prune -o -type d -exec chmod u+rwx,g+rx,o+rx {} +
find "${ROOT}" -path "${ROOT}/.git" -prune -o -type f -exec chmod u+rw,g+r,o+r {} +

# Keep git metadata writable by the repo owner.
chmod -R u+rwX "${ROOT}/.git" 2>/dev/null || true

# Restore executable bits for deployment scripts and common repo scripts.
if [[ -d "${ROOT}/deploy" ]]; then
  find "${ROOT}/deploy" -type f \( -name "*.sh" -o -name "*.bash" \) -exec chmod +x {} +
fi

if [[ -d "${ROOT}/scripts" ]]; then
  find "${ROOT}/scripts" -type f \( -name "*.sh" -o -name "*.bash" \) -exec chmod +x {} +
fi

# Python entrypoints can still be run as `python file.py`; mark common deploy entrypoints executable too.
find "${ROOT}/deploy" -maxdepth 2 -type f -name "*.py" -exec chmod u+x {} + 2>/dev/null || true

# Protect local deployment env files when present.
chmod 600 \
  "${ROOT}/deploy/azure/.env" \
  "${ROOT}/deploy/azure/service-env" \
  "${ROOT}/deploy/gcp/.env" \
  "${ROOT}/deploy/gcp/service-env" \
  2>/dev/null || true

# Ensure local env files do not get committed accidentally.
touch "${ROOT}/.gitignore"
for ignore_path in \
  "deploy/azure/.env" \
  "deploy/azure/service-env" \
  "deploy/gcp/.env" \
  "deploy/gcp/service-env" \
  "deploy/logs/" \
  "deploy/artifacts/"
do
  grep -qxF "${ignore_path}" "${ROOT}/.gitignore" || echo "${ignore_path}" >> "${ROOT}/.gitignore"
done

# If shell scripts are tracked, record their executable bit in git.
if command -v git >/dev/null 2>&1; then
  (
    cd "${ROOT}"
    while IFS= read -r -d '' f; do
      chmod +x "$f" 2>/dev/null || true
      git update-index --chmod=+x "$f" 2>/dev/null || true
    done < <(git ls-files -z '*.sh' ':!:*.ps1' 2>/dev/null || true)
  )
fi

echo "[ARGUS] Permission repair complete."
echo "[ARGUS] Next:"
echo "  git status --short"
echo "  bash deploy/azure/create-containerapps-resources.sh"
