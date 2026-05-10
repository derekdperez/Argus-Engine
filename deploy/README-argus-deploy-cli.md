# Argus Deploy CLI

`deploy/ArgusEngine.DeployUi` replaces the old Python terminal UI with a .NET command-line deployment console.

## Start the menu

```bash
./deploy/argus-deploy.sh
```

or:

```bash
dotnet run --project deploy/ArgusEngine.DeployUi -- menu
```

PowerShell:

```powershell
./deploy/argus-deploy.ps1
```

## Fast precise commands

```bash
# Incremental hot deploy using the existing optimized deploy backend.
./deploy/argus-deploy.sh local hot

# Image-based deploy when Dockerfile/build inputs changed.
./deploy/argus-deploy.sh local image

# Build and start only selected services.
./deploy/argus-deploy.sh local selected-image command-center-web worker-spider

# Recreate only selected services without rebuilding.
./deploy/argus-deploy.sh local selected-up command-center-web

# Status/logs/restart.
./deploy/argus-deploy.sh local status
./deploy/argus-deploy.sh local logs --follow worker-spider
./deploy/argus-deploy.sh local restart command-center-web
```

## AWS ECS / ECR

```bash
# EC2 hybrid mode: local core stack plus changed ECS workers.
./deploy/argus-deploy.sh ecs hybrid

# Build/push only selected images.
./deploy/argus-deploy.sh ecs build worker-spider worker-enum

# Deploy only selected ECS services.
./deploy/argus-deploy.sh ecs deploy worker-spider worker-enum

# Build, push, and deploy selected ECS services.
./deploy/argus-deploy.sh ecs release worker-spider worker-enum

# Status and autoscale.
./deploy/argus-deploy.sh ecs status
./deploy/argus-deploy.sh ecs autoscale
```

## Azure

The CLI loads environment variables from these files when present:

* `deploy/azure/.env`
* `argus-multicloud-deploy-scripts/deploy/azure/.env`
* `../argus-multicloud-deploy-scripts/deploy/azure/.env`

```bash
./deploy/argus-deploy.sh azure build worker-spider
./deploy/argus-deploy.sh azure deploy worker-spider
./deploy/argus-deploy.sh azure release worker-spider
./deploy/argus-deploy.sh azure status
```

The Azure commands call provider scripts such as `build-push-acr.sh` and `deploy-container-apps.sh` when they exist in the repo or the sibling multicloud deployment script checkout.

## Google Cloud

The CLI looks for provider scripts under `deploy/google`, `deploy/gcp`, or the sibling `argus-multicloud-deploy-scripts` checkout.

```bash
./deploy/argus-deploy.sh gcp build worker-spider
./deploy/argus-deploy.sh gcp deploy worker-spider
./deploy/argus-deploy.sh gcp release worker-spider
./deploy/argus-deploy.sh gcp status
```

## Change detection

```bash
./deploy/argus-deploy.sh changed
./deploy/argus-deploy.sh --base origin/main changed
```

The CLI maps changed files to services using `deploy/service-catalog.tsv` plus each service project's transitive `ProjectReference` closure. Global files such as `Directory.Packages.props`, `Directory.Build.props`, `global.json`, `deploy/docker-compose.yml`, and `deploy/service-catalog.tsv` intentionally affect all deployable services.

## Dry run

```bash
./deploy/argus-deploy.sh --dry-run ecs release worker-spider
```

Dry-run mode prints the exact commands that would be executed.
