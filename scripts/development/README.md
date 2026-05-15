# Argus Engine Development Scripts

`deploy.py` is the deployment source of truth. The scripts in this folder are local debugging conveniences only; they should not start, update, or deploy the stack.

## Main local deploy

From the repository root:

```bash
./deploy deploy --hot
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
./deploy deploy --hot

# Rebuild from scratch.
./deploy deploy --fresh

# Rebuild service images.
./deploy deploy --image

# Show status and logs.
./deploy status
./deploy logs --errors

# Restart a component.
./deploy restart command-center-web

# Delete local Compose containers and volumes.
./deploy --yes clean
```

## EC2 development notes

Open the EC2 security group for the ports you need:

- `8081` for the Gateway
- `15672` for RabbitMQ management, preferably restricted to your IP
- `22` for SSH

## Worker scale

```bash
./deploy scale local worker-spider=2 worker-enum=2
./deploy scale gcp worker-spider=2:10 worker-enum=2
```
