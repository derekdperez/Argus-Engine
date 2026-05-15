# Argus Engine Development Scripts

`deploy/deploy.py` is the deployment source of truth. The scripts in this folder are local debugging conveniences only; they should not start, update, or deploy the stack.

## Main local deploy

From the repository root:

```bash
python3 deploy/deploy.py deploy --hot
```

This starts or updates the full local Docker Compose stack:

- Postgres
- File-store database initializer
- Redis
- RabbitMQ
- Command Center services
- Gatekeeper
- Spider, HTTP requester, Enumeration, PortScan, HighValue, and Technology Identification workers

## Useful commands

```bash
# Deploy the whole stack locally.
python3 deploy/deploy.py deploy --hot

# Rebuild from scratch.
python3 deploy/deploy.py deploy --fresh

# Rebuild service images.
python3 deploy/deploy.py deploy --image

# Show status and logs.
python3 deploy/deploy.py status
python3 deploy/deploy.py logs --errors

# Restart a component.
python3 deploy/deploy.py restart command-center-web

# Delete local Compose containers and volumes.
python3 deploy/deploy.py --yes clean
```

## EC2 development notes

Open the EC2 security group for the ports you need:

- `8081` for the Gateway
- `15672` for RabbitMQ management, preferably restricted to your IP
- `22` for SSH

## Worker scale

```bash
python3 deploy/deploy.py scale local worker-spider=2 worker-enum=2
python3 deploy/deploy.py scale gcp worker-spider=2:10 worker-enum=2
```
