#!/usr/bin/env bash
set -euo pipefail

install_azure=0
install_gcp=0
do_login=0

if [[ "$#" -eq 0 ]]; then
  install_azure=1
  install_gcp=1
fi

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --azure) install_azure=1; shift ;;
    --gcp|--google) install_gcp=1; shift ;;
    --all) install_azure=1; install_gcp=1; shift ;;
    --login) do_login=1; shift ;;
    -h|--help)
      cat <<'EOF'
Usage: deploy/cloud-tools/install-cloud-clis.sh [--azure] [--gcp] [--all] [--login]

Installs Azure CLI and/or Google Cloud CLI using official package managers where possible.
Linux support targets Debian/Ubuntu apt-based hosts.
macOS support uses Homebrew.
EOF
      exit 0
      ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

has() { command -v "$1" >/dev/null 2>&1; }

sudo_if_needed() {
  if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

install_azure_linux_apt() {
  if has az; then
    echo "Azure CLI already installed: $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null || az --version | head -1)"
    return 0
  fi

  echo "Installing Azure CLI via Microsoft apt repository..."
  sudo_if_needed apt-get update
  sudo_if_needed apt-get install -y ca-certificates curl apt-transport-https lsb-release gnupg

  sudo_if_needed mkdir -p /etc/apt/keyrings
  curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
    | gpg --dearmor \
    | sudo_if_needed tee /etc/apt/keyrings/microsoft.gpg >/dev/null
  sudo_if_needed chmod go+r /etc/apt/keyrings/microsoft.gpg

  local az_dist
  az_dist="$(lsb_release -cs)"
  cat <<EOF | sudo_if_needed tee /etc/apt/sources.list.d/azure-cli.sources >/dev/null
Types: deb
URIs: https://packages.microsoft.com/repos/azure-cli/
Suites: ${az_dist}
Components: main
Architectures: $(dpkg --print-architecture)
Signed-by: /etc/apt/keyrings/microsoft.gpg
EOF

  sudo_if_needed apt-get update
  sudo_if_needed apt-get install -y azure-cli
}

install_gcp_linux_apt() {
  if has gcloud; then
    echo "Google Cloud CLI already installed: $(gcloud --version | head -1)"
    return 0
  fi

  echo "Installing Google Cloud CLI via Google apt repository..."
  sudo_if_needed apt-get update
  sudo_if_needed apt-get install -y apt-transport-https ca-certificates gnupg curl

  sudo_if_needed mkdir -p /usr/share/keyrings
  curl -fsSL https://packages.cloud.google.com/apt/doc/apt-key.gpg \
    | gpg --dearmor \
    | sudo_if_needed tee /usr/share/keyrings/cloud.google.gpg >/dev/null
  sudo_if_needed chmod go+r /usr/share/keyrings/cloud.google.gpg

  echo "deb [signed-by=/usr/share/keyrings/cloud.google.gpg] https://packages.cloud.google.com/apt cloud-sdk main" \
    | sudo_if_needed tee /etc/apt/sources.list.d/google-cloud-sdk.list >/dev/null

  sudo_if_needed apt-get update
  sudo_if_needed apt-get install -y google-cloud-cli
}

install_macos_brew() {
  local package="$1"
  if ! has brew; then
    echo "Homebrew is required on macOS. Install Homebrew first: https://brew.sh" >&2
    exit 2
  fi
  brew list "$package" >/dev/null 2>&1 || brew install "$package"
}

os_name="$(uname -s)"
if [[ "$os_name" == "Linux" ]]; then
  if ! has apt-get; then
    echo "This installer currently automates Debian/Ubuntu apt-based Linux only." >&2
    exit 2
  fi
  [[ "$install_azure" == "1" ]] && install_azure_linux_apt
  [[ "$install_gcp" == "1" ]] && install_gcp_linux_apt
elif [[ "$os_name" == "Darwin" ]]; then
  [[ "$install_azure" == "1" ]] && install_macos_brew azure-cli
  [[ "$install_gcp" == "1" ]] && install_macos_brew google-cloud-sdk
else
  echo "Unsupported OS: $os_name" >&2
  exit 2
fi

if [[ "$install_azure" == "1" ]]; then
  echo "Ensuring Azure Container Apps CLI extension is installed..."
  az extension add --name containerapp --upgrade >/dev/null
  az provider register --namespace Microsoft.App >/dev/null || true
  az provider register --namespace Microsoft.OperationalInsights >/dev/null || true
fi

if [[ "$do_login" == "1" ]]; then
  [[ "$install_azure" == "1" ]] && az login
  if [[ "$install_gcp" == "1" ]]; then
    gcloud auth login
    gcloud auth application-default login
  fi
fi

echo "CLI installation check complete."
