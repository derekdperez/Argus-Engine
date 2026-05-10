# Argus Engine DeployOps

This patch replaces deploy/deploy-ui.py with a standard-library Python interactive deployment and operations menu.

It wraps existing repository scripts:

- deploy/deploy.sh
- deploy/logs.sh
- deploy/smoke-test.sh
- deploy/dev-check.sh
- deploy/aws/*.sh

Start:

  ./deploy/deploy.sh

Or directly:

  python3 deploy/deploy-ui.py menu

Useful commands:

  python3 deploy/deploy-ui.py preflight
  python3 deploy/deploy-ui.py deploy --mode hot
  python3 deploy/deploy-ui.py deploy --mode image
  python3 deploy/deploy-ui.py deploy --mode fresh
  python3 deploy/deploy-ui.py update --mode hot
  python3 deploy/deploy-ui.py status
  python3 deploy/deploy-ui.py logs --errors
  python3 deploy/deploy-ui.py logs --follow worker-spider
  python3 deploy/deploy-ui.py restart command-center-web
  python3 deploy/deploy-ui.py smoke
  python3 deploy/deploy-ui.py backup config

AWS:

  python3 deploy/deploy-ui.py aws ecs-workers
  python3 deploy/deploy-ui.py aws ecr-push
  python3 deploy/deploy-ui.py aws deploy-services
  python3 deploy/deploy-ui.py aws autoscale
  python3 deploy/deploy-ui.py aws status
  python3 deploy/deploy-ui.py aws destroy-workers

Safety:

- The wrapper sets ARGUS_NO_UI=1 when calling deploy/deploy.sh to prevent recursive UI launches.
- Destructive actions use confirmation prompts.
- --dry-run prints commands without mutating Docker, Git, cloud, or database state.
- Logs are written to deploy/logs/deployops_<timestamp>.log.
