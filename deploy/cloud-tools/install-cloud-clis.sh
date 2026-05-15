#!/usr/bin/env bash
set -euo pipefail

INSTALL_AZURE=false
LOGIN=false

usage() {
  cat <<'EOF'
Usage:
  deploy/cloud-tools/install-cloud-clis.sh --azure [--login]

Installs Azure CLI on common Linux hosts, including Amazon Linux/RHEL-style dnf/yum hosts.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --azure) INSTALL_AZURE=true ;;
    --login) LOGIN=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage; exit 2 ;;
  esac
  shift
done

if [[ "$INSTALL_AZURE" != true ]]; then
  usage
  exit 0
fi

if command -v az >/dev/null 2>&1; then
  echo "Azure CLI already installed: $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null || az version)"
else
  if command -v apt-get >/dev/null 2>&1; then
    curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
  elif command -v dnf >/dev/null 2>&1; then
    . /etc/os-release || true
    RHEL_VERSION="9.0"
    if [[ "${VERSION_ID:-}" == "2" ]]; then
      RHEL_VERSION="7"
    fi
    sudo rpm --import https://packages.microsoft.com/keys/microsoft.asc
    sudo dnf install -y "https://packages.microsoft.com/config/rhel/${RHEL_VERSION}/packages-microsoft-prod.rpm"
    sudo dnf install -y azure-cli
  elif command -v yum >/dev/null 2>&1; then
    . /etc/os-release || true
    RHEL_VERSION="7"
    if [[ "${VERSION_ID:-}" == 2023* ]]; then
      RHEL_VERSION="9.0"
    fi
    sudo rpm --import https://packages.microsoft.com/keys/microsoft.asc
    sudo yum install -y "https://packages.microsoft.com/config/rhel/${RHEL_VERSION}/packages-microsoft-prod.rpm"
    sudo yum install -y azure-cli
  elif command -v brew >/dev/null 2>&1; then
    brew update && brew install azure-cli
  else
    echo "Unsupported OS/package manager. Install Azure CLI manually, then rerun this script." >&2
    exit 1
  fi
fi

az extension add --name containerapp --upgrade >/dev/null

if [[ "$LOGIN" == true ]]; then
  az login --use-device-code
fi

echo "Azure CLI ready."
az version --output table || true
