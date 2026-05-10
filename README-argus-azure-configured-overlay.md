# Argus Engine Azure deployment overlay

This overlay installs Azure deployment support directly under the project `deploy/` directory.

## Configured Azure values

- Subscription: `0f86d6db-2b36-47e8-ab8e-9ba7591f4d30`
- Location: `eastus`
- Resource group: `argus-engine-rg`
- Container Apps environment: `argus-engine-env`
- ACR name: `argusenginereg`
- ACR SKU: `Basic`
- Image prefix: `argus-engine`
- Image tag: `latest`
- Replicas: `1-3`

## Usage

From the Argus Engine project root:

```bash
unzip -o argus-azure-configured-overlay.zip
chmod +x deploy/cloud-common.sh deploy/cloud-tools/install-cloud-clis.sh deploy/azure/*.sh

./deploy/azure/doctor.sh
./deploy/cloud-tools/install-cloud-clis.sh --azure --login
./deploy/azure/create-containerapps-resources.sh
./deploy/azure/build-push-acr.sh
./deploy/azure/deploy-containerapps-workers.sh
```

`deploy/azure/.env` is local deployment config. The helper scripts add it to `.gitignore`.
`deploy/azure/service-env` contains runtime connection strings/secrets and should not be committed.
