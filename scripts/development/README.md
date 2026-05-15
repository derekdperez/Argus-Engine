# Argus Engine Development Scripts

These scripts are for running and debugging the entire Argus Engine stack on one development machine, including an EC2 instance.

## Main local deploy

From the repository root:

```bash
chmod +x deploy/deploy.py scripts/development/*.sh
./deploy/deploy.py
```

This starts the full local Docker Compose stack:

- Postgres
- File-store database initializer
- Redis
- RabbitMQ
- Command Center
- Gatekeeper
- Spider worker
- Enum worker
- PortScan worker
- HighValue worker
- Technology Identification worker

The script explicitly disables ECS workers and keeps everything on the local machine.

## Useful commands

```bash
# Deploy the whole stack locally.
./deploy/deploy.py

# Rebuild from scratch.
./deploy/deploy.py --fresh

# Deploy source-only changes into running containers when possible.
python3 deploy/deploy.py deploy --hot

# Wrapper for the misspelled name requested during triage.
./scripts/development/deploy_updatd_components.sh --hot

# Show host, Docker, API, queue, RabbitMQ, and Postgres state.
./scripts/development/show_application_state.sh

# Show EC2/Docker host logs plus application logs.
./scripts/development/show_development_machine_logs.sh

# Show recent error-like application logs.
./scripts/development/show_recent_errors.sh

# Follow one component's logs.
./scripts/development/show_development_machine_logs.sh --follow worker-spider

# Restart a component.
./scripts/development/restart_component.sh command-center

# Open a shell in a component container.
./scripts/development/shell_into_component.sh worker-spider sh

# Delete all local data and volumes.
CONFIRM_RESET_ARGUS_LOCAL=yes ./scripts/development/reset_local_data.sh
```

## EC2 development notes

Open the EC2 security group for the ports you need:

- `8080` for Command Center
- `15672` for RabbitMQ management, preferably restricted to your IP
- `22` for SSH

Set `ARGUS_LOCAL_PUBLIC_HOST` if automatic EC2 public IP detection is blocked:

```bash
ARGUS_LOCAL_PUBLIC_HOST=<ec2-public-ip> ./deploy/deploy.py
```

## Worker scale

Defaults are intentionally small for development: one local container for each worker service.

Override scale when needed:

```bash
ARGUS_LOCAL_SCALE_WORKER_SPIDER=2 \
ARGUS_LOCAL_SCALE_WORKER_ENUM=2 \
./deploy/deploy.py
```

Or pass flags:

```bash
./deploy/deploy.py --scale-spider 2 --scale-enum 2
```
