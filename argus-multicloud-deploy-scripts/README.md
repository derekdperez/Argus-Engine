# Argus Engine multi-cloud worker deployment helpers

This overlay adds Azure Container Apps and Google Cloud Run Worker Pool deployment helpers that mirror the intent of the existing `deploy/aws` ECS worker scripts.

The scripts intentionally deploy **Argus background workers** only. Keep Postgres, Redis, RabbitMQ, and Command Center reachable from those workers through the values in each cloud's `service-env` file. This matches the existing ECS-worker model without forcing a database or broker migration.

## Files

```text
deploy/cloud-common.sh
deploy/cloud-tools/install-cloud-clis.sh

deploy/azure/.env.example
deploy/azure/service-env.example
deploy/azure/create-containerapps-resources.sh
deploy/azure/build-push-acr.sh
deploy/azure/deploy-containerapps-workers.sh
deploy/azure/destroy-containerapps-workers.sh

deploy/gcp/.env.example
deploy/gcp/service-env.example
deploy/gcp/create-artifact-registry.sh
deploy/gcp/build-push-artifact-registry.sh
deploy/gcp/deploy-cloudrun-worker-pools.sh
deploy/gcp/destroy-cloudrun-worker-pools.sh
```

## Install into a repo clone

From the directory containing this README:

```bash
./install-into-repo.sh /path/to/Argus-Engine
```

Then from the repo root:

```bash
chmod +x deploy/cloud-tools/install-cloud-clis.sh deploy/azure/*.sh deploy/gcp/*.sh
```

## Shared assumptions

These scripts expect:

- Docker is installed and the daemon is running.
- You are running from the Argus repo root.
- `deploy/build-base-images.sh` exists and can build `argus-engine-base:local`.
- The cloud workers can reach the Postgres, FileStore Postgres, Redis, RabbitMQ, and optional RabbitMQ management endpoints configured in `service-env`.
- The default worker list is:

```text
gatekeeper
command-center-spider-dispatcher
worker-spider
worker-http-requester
worker-enum
worker-portscan
worker-highvalue
worker-techid
```

Override the list with either command arguments or `ARGUS_CLOUD_SERVICES`.

## Azure quick start

```bash
cp deploy/azure/.env.example deploy/azure/.env
cp deploy/azure/service-env.example deploy/azure/service-env

# Edit deploy/azure/.env and deploy/azure/service-env first.
deploy/cloud-tools/install-cloud-clis.sh --azure
az login

set -a
. deploy/azure/.env
set +a

deploy/azure/create-containerapps-resources.sh
deploy/azure/build-push-acr.sh
deploy/azure/deploy-containerapps-workers.sh
```

Destroy only the worker Container Apps:

```bash
CONFIRM_DESTROY_AZURE_ARGUS_WORKERS=yes deploy/azure/destroy-containerapps-workers.sh
```

## Google Cloud quick start

```bash
cp deploy/gcp/.env.example deploy/gcp/.env
cp deploy/gcp/service-env.example deploy/gcp/service-env

# Edit deploy/gcp/.env and deploy/gcp/service-env first.
deploy/cloud-tools/install-cloud-clis.sh --gcp
gcloud auth login
gcloud auth application-default login

set -a
. deploy/gcp/.env
set +a

deploy/gcp/create-artifact-registry.sh
deploy/gcp/build-push-artifact-registry.sh
deploy/gcp/deploy-cloudrun-worker-pools.sh
```

Destroy only the Cloud Run Worker Pools:

```bash
CONFIRM_DESTROY_GCP_ARGUS_WORKERS=yes deploy/gcp/destroy-cloudrun-worker-pools.sh
```

## Networking note

For a first deployment, use routable private endpoints and cloud networking:

- Azure: place Container Apps in an environment/subnet that can reach your database and broker endpoints, or expose tightly firewalled endpoints.
- Google Cloud: set `GCP_VPC_CONNECTOR` and `GCP_VPC_EGRESS` if workers need private VPC access.

Do not leave databases or brokers publicly open. The service-env files are deliberately examples, not secure production secrets management.
