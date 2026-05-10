# Argus Azure Interactive Deploy Overlay

This overlay installs Azure deployment scripts directly into the Argus Engine repo under `deploy/azure`.

## Install

From the Argus repo root:

```bash
unzip -o argus-azure-interactive-deploy-overlay.zip
./install-argus-azure-deploy-overlay.sh .
```

Or from anywhere:

```bash
./install-argus-azure-deploy-overlay.sh /home/ec2-user/argus-engine
```

## Run

```bash
cd /home/ec2-user/argus-engine

./deploy/azure/doctor.sh
./deploy/cloud-tools/install-cloud-clis.sh --azure --login

./deploy/azure/create-containerapps-resources.sh
./deploy/azure/build-push-acr.sh
./deploy/azure/deploy-containerapps-workers.sh
```

The scripts now prompt for missing Azure config and write it to:

- `deploy/azure/.env`
- `deploy/azure/service-env`

Those local files are added to `.gitignore` by the helper because they can contain secrets.

## Common mistake fixed

Do not run scripts from:

```bash
./argus-multicloud-deploy-scripts/deploy/azure/build-push-acr.sh
```

That is just an unpacked helper directory. Install this overlay and run:

```bash
./deploy/azure/build-push-acr.sh
```
