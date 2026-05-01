# AWS ECS deployment helpers for NightmareV2

These helpers keep the production stack self-hosted: Postgres, Redis, and RabbitMQ remain containers, while the .NET services run as independently scalable ECS services.

## Intent

- No Amazon RDS, ElastiCache, Amazon MQ, or paid custom CloudWatch metrics are required.
- Container images are pushed to ECR.
- ECS services are created and updated by `deploy-ecs-services.sh`.
- ECS worker services are scaled with `autoscale-ecs-workers.sh`, which reads Command Center queue APIs and calls `aws ecs update-service`.
- `docker-compose.yml` remains the local development deployment.

## Required AWS resources

The scripts assume these already exist:

- An ECS cluster.
- VPC subnets and security groups that can reach your Postgres, Redis, RabbitMQ, and Command Center endpoints.
- An ECS task execution role with ECR pull and CloudWatch Logs permissions.
- Optional ECS task role if the containers need AWS API access at runtime.
- ECR repositories created by `create-ecr-repos.sh`.

The scripts intentionally do not create the VPC, cluster, load balancer, or database/broker hosts because those choices are environment-specific.

## ECS deploy workflow

Create local, non-committed config files:

```bash
cp deploy/aws/.env.example deploy/aws/.env
cp deploy/aws/service-env.example deploy/aws/service-env
```

Edit `deploy/aws/.env` with your AWS account, cluster, subnet, security group, and IAM role values.
Edit `deploy/aws/service-env` with production Postgres, Redis, and RabbitMQ connection values. Do not point ECS tasks at the example `postgres`, `redis`, or `rabbitmq` hostnames unless those names resolve inside your ECS VPC.

Build and push the current code:

```bash
set -a
. deploy/aws/.env
set +a
deploy/aws/create-ecr-repos.sh
deploy/aws/build-push-ecr.sh
```

Register task definitions and create or update ECS services:

```bash
deploy/aws/deploy-ecs-services.sh
```

To deploy only selected services:

```bash
deploy/aws/deploy-ecs-services.sh worker-spider worker-enum worker-portscan
```

By default, updates preserve existing desired counts and force a new deployment onto the newly registered task definition. Set `UPDATE_DESIRED_COUNTS=true` when you want the script to also apply `ECS_DESIRED_*` counts from `.env`.

## Queue-driven ECS scaling

Run this from cron, a small always-on host, or a scheduled ECS task:

```bash
set -a
. deploy/aws/.env
set +a
deploy/aws/autoscale-ecs-workers.sh
```

The scaler handles:

- `worker-spider` from `/api/http-request-queue/metrics`.
- `worker-enum`, `worker-portscan`, `worker-highvalue`, and `worker-techid` from `/api/ops/rabbit-queues`.

It scales desired counts between each worker's `ECS_MIN_*` and `ECS_MAX_*` values. Minimums default to `1` so each worker type stays warm; set a worker minimum to `0` if you want it destroyed when idle.

The scaler also keeps services on the newest active task-definition revision by default (`ECS_AUTOSCALER_UPDATE_TASK_DEFINITION=true`). That lets a scheduled scaler roll workers forward after `deploy-ecs-services.sh` registers a newer task definition.

## Destroy worker services

Delete only worker ECS services:

```bash
CONFIRM_DESTROY_ECS_WORKERS=yes deploy/aws/destroy-ecs-services.sh workers
```

Delete all Nightmare ECS services:

```bash
CONFIRM_DESTROY_ECS_ALL=yes deploy/aws/destroy-ecs-services.sh all
```

This deletes ECS services only. It does not delete ECR repositories, log groups, databases, brokers, VPC resources, or task definition revisions.

## HTTP Request Worker Scaling

For high-throughput HTTP request processing, NightmareV2 supports distributed worker deployment:

**📖 See [SCALING_GUIDE.md](./SCALING_GUIDE.md) for comprehensive documentation on:**
- Local docker-compose deployment with 10 workers
- EC2-based worker provisioning and deployment
- Domain-level request rate limiting (1 per domain per minute)
- Worker health monitoring and troubleshooting

**Quick start:**

```bash
# Provision 2 new EC2 instances with 10 workers each
set -a
. deploy/aws/.env
set +a
deploy/aws/provision-ec2-workers.sh

# Deploy and start workers once instances are running
deploy/aws/deploy-worker-instances.sh <instance-id-1> <instance-id-2>

# Verify workers are running
curl http://${COMMAND_CENTER_URL}/api/http-request-queue/metrics
```

## Required environment

Copy `.env.example` to `.env` and set the values for your AWS account, region, cluster, and repositories.

## Build and push images

```bash
cd DotNetSolution
set -a
. deploy/aws/.env
set +a
deploy/aws/build-push-ecr.sh
```

## Legacy single-service HTTP scaler

`autoscale-http-workers.sh` is kept as a compatibility wrapper around the new ECS scaler for `worker-spider` only:

```bash
set -a
. deploy/aws/.env
set +a
deploy/aws/autoscale-http-workers.sh
```

New deployments should prefer `autoscale-ecs-workers.sh` because it scales spider, subdomain enum, port scan, high-value, and technology identification workers.

## Subdomain enumeration tooling

`deploy/Dockerfile.worker` builds `subfinder` and `amass` into every worker image so the `worker-enum` ECS service can run both tools without host-level installation.

Defaults:

- `subfinder` runs with all passive sources enabled and recursive discovery.
- `amass` runs active enumeration with brute-force enabled against the bundled wordlist at `/opt/nightmare/wordlists/subdomains.txt`.
- DNS fallback remains enabled so basic enumeration still works if a tool fails or times out.

When changing tool versions, set these before running `deploy/aws/build-push-ecr.sh`:

```bash
export SUBFINDER_PACKAGE=github.com/projectdiscovery/subfinder/v2/cmd/subfinder@latest
export AMASS_PACKAGE=github.com/owasp-amass/amass/v5/cmd/amass@main
```

For ECS, copy the `Enumeration__*` variables from `deploy/aws/service-env.example` into the `worker-enum` task definition.

## ECS service model

Recommended services:

- command-center
- gatekeeper
- worker-spider
- worker-enum
- worker-portscan
- worker-highvalue
- worker-techid

Keep Postgres, Redis, and RabbitMQ self-hosted in your own ECS/EC2 containers unless you decide to move to managed AWS services later.
