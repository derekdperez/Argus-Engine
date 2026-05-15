# AWS ECS deployment helpers for argusV2

These helpers keep the production stack self-hosted: Postgres, Redis, and RabbitMQ remain containers, while the .NET services run as independently scalable ECS services.

## Intent

- No Amazon RDS, ElastiCache, Amazon MQ, or paid custom CloudWatch metrics are required.
- Container images are pushed to ECR.
- ECS services are created and updated by `deploy-ecs-services.sh`.
- ECS worker services are scaled with `deploy.py ecs autoscale`, which reads Command Center queue APIs and calls `aws ecs update-service`.
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

## Main branch release gate

`.github/workflows/release-main.yml` is the production path for pushes to `main`:

1. Restore and build `ArgusEngine.slnx`.
2. Run the unit test suite through `./test.sh unit`.
3. Ensure the long-lived EC2 E2E test server exists, hot-deploy the pushed Git SHA to it, recreate the E2E databases, and run `src/tests/e2e/run-e2e-suite.sh`.
4. Build and push ECR images tagged with the Git SHA.
5. Register/update ECS services with the new image tag.

The older `publish-ecr.yml` workflow is manual only so `main` cannot publish images before E2E has passed.

Required GitHub configuration:

- `ARGUS_AWS_GITHUB_ACTIONS_ROLE` repository variable, or the default role in the workflow.
- `ARGUS_ECS_SERVICE_ENV` repository secret containing the production service env file contents.
- ECS variables such as `ECS_CLUSTER`, `ECS_SUBNETS`, `ECS_SECURITY_GROUPS`, and `ECS_TASK_EXECUTION_ROLE_ARN`.
- E2E variables such as `ARGUS_E2E_SUBNET_ID`, `ARGUS_E2E_SECURITY_GROUP`, and `ARGUS_E2E_IAM_INSTANCE_PROFILE`.

The E2E host should use an instance profile with SSM managed-instance permissions. It is reused between builds, but `src/tests/e2e/reset-e2e-database.sh` drops and recreates `argus_engine` and `argus_engine_files` before every run. Set `ARGUS_E2E_DB_SNAPSHOT_SQL` to a SQL file path on that host when you want to restore a richer snapshot instead of the default empty bootstrapped database.

### EC2 one-command worker deployment

When running the self-hosted stack on one EC2 host and placing workers on ECS, use:

```bash
python3 deploy/deploy.py deploy --ecs-workers
```

That mode:

- Starts Postgres, Redis, RabbitMQ, Command Center, and Gatekeeper locally with Docker Compose.
- Scales local worker containers to zero so ECS owns worker capacity.
- Uses EC2 metadata to discover the current VPC, subnet, security groups, private IP, region, and account.
- Creates the ECS cluster, ECR repositories, CloudWatch log group, ECS task execution role, task role, and ECS worker security group when missing.
- Opens inbound access from the ECS worker security group to the EC2 host security group on `5432`, `6379`, `5672`, `15672`, and `8080`.
- Generates `deploy/aws/service-env` so ECS workers reach the self-hosted compose services through the EC2 private IP.
- Builds/pushes ECR images.
- Scales any existing ECS worker services to zero and waits for the old worker tasks to stop.
- Creates or updates ECS worker services and scales them back up on the newest task definitions/images.
- Records ECS worker and current EC2 host usage samples for the Command Center Admin page.

The mode is designed to be re-runnable. Existing roles, cluster, repositories, log groups, security group rules, generated env files, task definitions, and ECS services are reused. The worker tasks themselves are intentionally replaced on each `--ecs-workers` deploy (`argus_ECS_REPLACE_WORKERS=1` by default) so each deploy exercises worker loss/recovery and guarantees new worker containers start from the latest pushed image. Set `argus_ECS_REPLACE_WORKERS=0` to converge in place without the scale-to-zero replacement step.

`--ecs-workers` defaults `argus_GIT_PULL=1`, so it runs `git pull --ff-only` before building. Disable that only when you intentionally want to deploy the checked-out working tree:

```bash
argus_GIT_PULL=0 python3 deploy/deploy.py deploy --ecs-workers
```

Required before running:

- The EC2 instance profile must allow ECS, ECR, IAM role creation/attachment, CloudWatch Logs, STS, and EC2 describe/security-group updates.
- The EC2 instance security group must permit your browser to reach Command Center on `8080` if you want public UI access.
- The EC2 instance must have enough disk/CPU to build Docker images.

For continuous scaling, run this periodically after the first deploy:

```bash
set -a
. deploy/aws/.env.generated
set +a
deploy/aws/deploy.py ecs autoscale
```

Run the scaler on a steady cadence, such as every minute from cron or systemd timer, because the Admin usage totals integrate worker-hours from these samples.

The manual workflow below is still useful when the core services are already reachable from ECS through managed databases/brokers or custom network plumbing.

## Manual EC2 worker controls

The Operations page also has an `EC2 Workers` section for launching and scaling worker-only EC2 machines independently from ECS. Command Center persists the machine list, enforces a maximum of two active EC2 worker machines, launches instances with the configured AMI/subnet/security group, and uses AWS Systems Manager Run Command to apply per-machine worker counts.

Set the `EC2_WORKER_*` values in the Command Center environment before using the page. At minimum, configure the region, instance profile or role, subnet/security group, repository URL/branch, and routable service endpoints for Postgres, Redis, and RabbitMQ. The EC2 worker instance profile must allow SSM Managed Instance Core so Command Center can send scale commands after the instance boots.

Remote EC2 worker machines run `deploy/docker-compose.ec2-workers.yml` through `deploy/deploy.py scale local`. They do not run Postgres, Redis, RabbitMQ, Command Center, or Gatekeeper locally; they connect back to the configured core host.

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
deploy/deploy.py ecs repos
deploy/deploy.py ecs build
```

Register task definitions and create or update ECS services:

```bash
deploy/deploy.py ecs deploy
```

To deploy only selected services:

```bash
deploy/deploy.py ecs deploy worker-spider worker-enum worker-portscan
```

By default, updates preserve existing desired counts and force a new deployment onto the newly registered task definition. Set `UPDATE_DESIRED_COUNTS=true` when you want the script to also apply `ECS_DESIRED_*` counts from `.env`.

## Queue-driven ECS scaling

Run this from cron, a small always-on host, or a scheduled ECS task:

```bash
set -a
. deploy/aws/.env
set +a
deploy/aws/deploy.py ecs autoscale
```

The scaler handles:

- `worker-spider` from `/api/http-request-queue/metrics`.
- `worker-enum`, `worker-portscan`, `worker-highvalue`, and `worker-techid` from `/api/ops/rabbit-queues`.

It scales desired counts between each worker's `ECS_MIN_*` and `ECS_MAX_*` values. Minimums default to `1` so each worker type stays warm; set a worker minimum to `0` if you want it destroyed when idle.

The scaler also keeps services on the newest active task-definition revision by default (`ECS_AUTOSCALER_UPDATE_TASK_DEFINITION=true`). That lets a scheduled scaler roll workers forward after `deploy-ecs-services.sh` registers a newer task definition.

By default the scaler records a usage sample after every run (`ECS_RECORD_USAGE_SAMPLE=true`). Command Center reads those samples on `/admin` and `/api/admin/usage` to estimate monthly ECS worker hours against the 2200-hour allowance, EC2 server hours, and application-level HTTP request/response bytes from the HTTP queue. The HTTP bandwidth figure is an application estimate from stored request/response data, not an AWS billing-metered VPC/NAT total.

## Destroy worker services

Delete only worker ECS services:

```bash
CONFIRM_DESTROY_ECS_WORKERS=yes deploy/deploy.py ecs teardown workers
```

Delete all argus ECS services:

```bash
CONFIRM_DESTROY_ECS_ALL=yes deploy/deploy.py ecs teardown all
```

This deletes ECS services only. It does not delete ECR repositories, log groups, databases, brokers, VPC resources, or task definition revisions.

## HTTP Request Worker Scaling

For high-throughput HTTP request processing, argusV2 supports distributed worker deployment:

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
deploy/deploy.py ecs provision-workers

# Deploy and start workers once instances are running
deploy/deploy.py ecs deploy-workers <instance-id-1> <instance-id-2>

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
deploy/deploy.py ecs build
```

## Legacy single-service HTTP scaler

`autoscale-http-workers.sh` is kept as a compatibility wrapper around the new ECS scaler for `worker-spider` only:

```bash
set -a
. deploy/aws/.env
set +a
deploy/deploy.py scale autoscale
```

New deployments should prefer `deploy.py ecs autoscale` because it scales spider, subdomain enum, port scan, high-value, and technology identification workers.

## Subdomain enumeration tooling

`deploy/Dockerfile.worker-enum` copies committed `subfinder` and `amass` Linux binaries into the `worker-enum` image so ECS builds do not compile or download the tool projects during deployment.

Defaults:

- `subfinder` runs with all passive sources enabled and recursive discovery.
- `amass` runs active enumeration with brute-force enabled against the bundled wordlist at `/opt/argus/wordlists/subdomains.txt`.
- DNS fallback remains enabled so basic enumeration still works if a tool fails or times out.

When changing tool versions, refresh and commit the vendored artifacts before running `deploy/deploy.py ecs build`:

```bash
export SUBFINDER_VERSION=2.14.0
export AMASS_VERSION=5.1.1
./deploy/deploy.py gcp build
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
