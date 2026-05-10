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

Linux support:
  - Debian/Ubuntu: apt
  - Amazon Linux / RHEL / CentOS / Fedora-family: dnf or yum

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

pkg_mgr() {
  if has apt-get; then echo apt; return; fi
  if has dnf; then echo dnf; return; fi
  if has yum; then echo yum; return; fi
  echo ""
}

detect_rhel_repo_version() {
  local version_id=""
  if [[ -r /etc/os-release ]]; then
    # shellcheck source=/dev/null
    . /etc/os-release
    version_id="${VERSION_ID:-}"
  fi

  case "$version_id" in
    2|2.*|7|7.*) echo "7" ;;
    2023|8|8.*|9|9.*|"") echo "9.0" ;;
    10|10.*) echo "9.0" ;; # Azure CLI RHEL 9 repo is the safest current fallback for RHEL-compatible hosts.
    *) echo "9.0" ;;
  esac
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

install_azure_linux_rpm() {
  if has az; then
    echo "Azure CLI already installed: $(az version --query '\"azure-cli\"' -o tsv 2>/dev/null || az --version | head -1)"
    return 0
  fi

  local mgr rhel_repo
  mgr="$(pkg_mgr)"
  rhel_repo="$(detect_rhel_repo_version)"

  echo "Installing Azure CLI via Microsoft RPM repository..."
  sudo_if_needed rpm --import https://packages.microsoft.com/keys/microsoft.asc
  sudo_if_needed "$mgr" install -y "https://packages.microsoft.com/config/rhel/${rhel_repo}/packages-microsoft-prod.rpm"
  sudo_if_needed "$mgr" install -y azure-cli
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

install_gcp_linux_rpm() {
  if has gcloud; then
    echo "Google Cloud CLI already installed: $(gcloud --version | head -1)"
    return 0
  fi

  local mgr arch repo_suffix gpgkey
  mgr="$(pkg_mgr)"
  arch="$(uname -m)"

  case "$arch" in
    aarch64|arm64) repo_suffix="aarch64" ;;
    *) repo_suffix="x86_64" ;;
  esac

  gpgkey="https://packages.cloud.google.com/yum/doc/rpm-package-key.gpg"

  echo "Installing Google Cloud CLI via Google RPM repository..."
  cat <<EOF | sudo_if_needed tee /etc/yum.repos.d/google-cloud-sdk.repo >/dev/null
[google-cloud-cli]
name=Google Cloud CLI
baseurl=https://packages.cloud.google.com/yum/repos/cloud-sdk-el9-${repo_suffix}
enabled=1
gpgcheck=1
repo_gpgcheck=0
gpgkey=${gpgkey}
EOF

  # Required on some RHEL 9-compatible hosts. Not present/needed everywhere.
  sudo_if_needed "$mgr" install -y libxcrypt-compat."${repo_suffix}" >/dev/null 2>&1 || true
  sudo_if_needed "$mgr" install -y google-cloud-cli
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
  mgr="$(pkg_mgr)"
  case "$mgr" in
    apt)
      [[ "$install_azure" == "1" ]] && install_azure_linux_apt
      [[ "$install_gcp" == "1" ]] && install_gcp_linux_apt
      ;;
    dnf|yum)
      [[ "$install_azure" == "1" ]] && install_azure_linux_rpm
      [[ "$install_gcp" == "1" ]] && install_gcp_linux_rpm
      ;;
    *)
      echo "Unsupported Linux package manager. Install az/gcloud manually, then rerun." >&2
      exit 2
      ;;
  esac
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
  [[ "$install_azure" == "1" ]] && az login --use-device-code
  if [[ "$install_gcp" == "1" ]]; then
    gcloud auth login --no-launch-browser
    gcloud auth application-default login --no-launch-browser || true
  fi
fi

echo "Cloud CLI installation complete."
