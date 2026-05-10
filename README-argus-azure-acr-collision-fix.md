# Argus Azure ACR collision fix overlay

This overlay patches the Azure deployment scripts so they do not fail when the configured
Azure Container Registry name is already taken globally.

Changes:

- Detects whether the configured ACR exists in the configured resource group.
- Uses `az acr check-name` before creating a new registry.
- Prompts for a replacement ACR name if the current one is taken by another Azure tenant/subscription.
- Writes the selected registry name back to `deploy/azure/.env`.
- Rejects `CHANGE_ME`, `changeme`, and `10.0.0.10` placeholders in interactive runtime prompts.

Installed config defaults to:

```bash
AZURE_ACR_NAME='argusengine0f86d6db'
```

If that is unavailable too, the script prompts for another globally unique name.

## Install

From the Argus repo root:

```bash
unzip -o argus-azure-acr-collision-fix-overlay.zip
chmod +x deploy/cloud-common.sh deploy/cloud-tools/*.sh deploy/azure/*.sh
```

## Run

```bash
bash deploy/azure/create-containerapps-resources.sh
bash deploy/azure/build-push-acr.sh
bash deploy/azure/deploy-containerapps-workers.sh
```
