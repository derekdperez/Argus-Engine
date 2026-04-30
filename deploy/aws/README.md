# AWS ECS deployment helpers for NightmareV2

These helpers keep the production stack self-hosted: Postgres, Redis, and RabbitMQ remain containers, while the .NET services run as independently scalable ECS services.

## Intent

- No Amazon RDS, ElastiCache, Amazon MQ, or paid custom CloudWatch metrics are required.
- Container images are pushed to ECR.
- ECS services are scaled with the included autoscaler script, which reads the app's own HTTP queue API and calls `aws ecs update-service`.
- `docker-compose.yml` remains the local development deployment.

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

## Scale HTTP workers from the app queue

Run this from a small always-on host, a cron job, or a scheduled ECS task:

```bash
set -a
. deploy/aws/.env
set +a
deploy/aws/autoscale-http-workers.sh
```

The autoscaler reads:

```txt
${COMMAND_CENTER_URL}/api/http-request-queue
```

and adjusts the spider worker ECS service because the spider worker drains the durable HTTP request queue.



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
