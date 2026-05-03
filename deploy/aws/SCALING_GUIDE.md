# Worker Scaling Guide

This guide explains how to scale argusV2 workers to support high-throughput reconnaissance scanning. The system supports local Docker Compose, ECS services, and the older distributed EC2 worker deployment.

## Architecture Overview

### ECS service model

Production ECS deployments use one independently scalable service per worker class:

| ECS service key | Work source | Scaling signal |
|---|---|---|
| `worker-spider` | Durable Postgres HTTP request queue | `/api/http-request-queue/metrics` backlog |
| `worker-enum` | RabbitMQ subdomain enumeration jobs | `/api/ops/rabbit-queues` ready + unacknowledged messages for `Enum` |
| `worker-portscan` | RabbitMQ port scan jobs | `/api/ops/rabbit-queues` messages for `PortScan` |
| `worker-highvalue` | RabbitMQ high-value regex/path work | `/api/ops/rabbit-queues` messages for `HighValueRegex` and `HighValuePaths` |
| `worker-techid` | RabbitMQ technology identification work | `/api/ops/rabbit-queues` messages for `TechnologyIdentification` |

`deploy/aws/deploy-ecs-services.sh` registers a fresh task definition from the current ECR image tag and creates or updates ECS services. `deploy/aws/autoscale-ecs-workers.sh` recalculates each worker service desired count and can move services to the newest active task-definition revision.

### Worker Configuration

- **Max Concurrency**: 10 concurrent HTTP requests per worker (configurable in database)
- **Domain Locking**: Workers automatically limit to 1 active request per domain per minute
- **Per-Instance Capacity**: Each EC2 instance runs 10 worker containers = 100 concurrent requests max per instance
- **Scaling Model**: Horizontal scaling via additional EC2 instances

### Domain-Level Request Limiting

The HTTP request queue implements per-domain rate limiting to prevent overwhelming target domains:

**Key Configuration** (in `HttpRequestQueueSettings`):
- `PerDomainRequestsPerMinute`: 6 (default) - Maximum requests per domain per minute
- `GlobalRequestsPerMinute`: 120 (default) - Maximum global requests per minute
- `MaxConcurrency`: 10 (default) - Maximum concurrent requests across all workers

**SQL Implementation** (in `HttpRequestQueueWorker.TryLeaseNextAsync`):

The worker's lease acquisition query includes these domain-level checks:

```sql
AND (
    SELECT COUNT(*)
    FROM http_request_queue recent_domain
    WHERE recent_domain.domain_key = q.domain_key
      AND recent_domain.started_at_utc IS NOT NULL
      AND recent_domain.started_at_utc >= @one_minute_ago
) < @per_domain_requests_per_minute
```

This ensures:
1. **One domain per worker rule**: Each worker processes requests from different domains to maintain diversity
2. **Rate limiting**: Per-domain request count is checked before leasing  
3. **Adaptive throttling**: Failed requests automatically reduce concurrency (see `AdaptiveConcurrencyController`)

### Request Locking Mechanism

Requests are locked using pessimistic locking in the database:
- Lock holder ID: `worker_id` (format: `{MachineName}:{ProcessId}:{Guid}`)
- Lock duration: 5 minutes per request
- Expired locks are reaped and requests are retried
- Lock status: `state = 'InFlight'`

This ensures:
- No two workers process the same request
- Locks are holder-specific so failed workers don't block requests forever
- Expired locks fail gracefully with retry

## Local Development Setup

### Single Machine with 10 Workers

Update the local deployment to run 10 worker replicas:

```bash
./deploy/run-local.sh        # Deploys with 10 worker replicas (docker-compose.yml has deploy.replicas: 10)
```

The `docker-compose.yml` now includes:

```yaml
worker-spider:
  # ... service definition ...
  deploy:
    replicas: 10
```

Monitor the HTTP queue:

```bash
curl http://localhost:8080/api/http-request-queue/metrics
```

Example response:

```json
{
  "backlogCount": 150,
  "inFlightCount": 10,
  "failedCount": 2,
  "totalProcessed": 1250
}
```

## EC2 Deployment Setup

The EC2 deployment path is retained for direct Docker worker fleets. Prefer ECS for new production worker scaling unless you specifically need host-level control.

### Prerequisites

1. **AWS CLI** configured with appropriate credentials
2. **SSH key pair** created in AWS (e.g., `kp1`)
3. **EC2 AMI**: Ubuntu 24.04 LTS (ami-0c55b159cbfafe1f0 or later in your region)
4. **Default VPC** with subnet and security group
5. **IAM role** for EC2 instances (optional, with ECR pull permissions)

### Configuration

Create or update `deploy/aws/.env`:

```bash
# Copy the template
cp deploy/aws/.env.example deploy/aws/.env

# Edit with your values
export AWS_REGION=us-east-1
export AWS_ACCOUNT_ID=123456789012
export WORKER_INSTANCE_TYPE=m7i-flex.large
export WORKER_KEY_PAIR=kp1
export WORKER_IAM_ROLE=ec2-iam
export WORKER_SECURITY_GROUP=default
export WORKER_COUNT=10
export INSTANCE_COUNT=2
export SSH_KEY_PATH=~/.ssh/kp1.pem
export SSH_USER=ubuntu
export COMMAND_CENTER_URL=http://10.0.0.100:8080
```

### Step 1: Provision EC2 Instances

```bash
set -a
. deploy/aws/.env
set +a

# Provision 2 new instances (each will run 10 workers)
deploy/aws/provision-ec2-workers.sh
```

This script:
- Creates 2 EC2 instances of type `m7i-flex.large`
- Uses the `kp1` key pair for SSH access
- Assigns the default security group
- Installs Docker and Docker Compose via user data
- Clones the argusV2 repository to `/opt/argus`

Wait for instances to reach 'running' state:

```bash
watch -n 2 aws ec2 describe-instances \
  --region us-east-1 \
  --instance-ids i-0123456789abcdef0 i-0123456789abcdef1 \
  --query 'Reservations[].Instances[].[Tags[?Key==`Name`].Value[],State.Name,PrivateIpAddress]' \
  --output table
```

### Step 2: Deploy Workers to Instances

Once instances are running:

```bash
# Deploy 10 workers to each instance
deploy/aws/deploy-worker-instances.sh i-0123456789abcdef0 i-0123456789abcdef1

# Or with comma-separated IDs
deploy/aws/deploy-worker-instances.sh i-0123456789abcdef0,i-0123456789abcdef1
```

This script on each instance:
1. Pulls latest code from main branch
2. Builds the worker Docker image
3. Stops existing workers
4. Starts 10 new worker containers with docker-compose
5. Verifies workers are running

Monitor deployment progress:

```bash
# Watch worker pod count across all instances
watch -n 3 'for id in i-0123456789abcdef0 i-0123456789abcdef1; do \
  echo "Instance $id:"; \
  aws ec2-instance-connect send-command --instance-ids "$id" \
    --document-name "AWS-RunShellScript" \
    --parameters "commands=[\"docker ps -q -f label=com.docker.compose.service=worker-spider | wc -l\"]" \
    --output text | tail -1; \
done'
```

### Step 3: Verify Workers are Running

Check the HTTP request queue metrics:

```bash
curl http://${COMMAND_CENTER_URL}/api/http-request-queue/metrics

# Expected output with 20 workers running (2 instances × 10):
# {
#   "backlogCount": 200,
#   "inFlightCount": 20,
#   "failedCount": 0,
#   "totalProcessed": 5000
# }
```

View worker logs from an instance:

```bash
# SSH into instance
ssh -i ~/.ssh/kp1.pem ubuntu@<instance-ip>

# View worker logs
cd /opt/argus
docker compose -f deploy/docker-compose.yml logs -f worker-spider
```

## Capacity Planning

With m7i-flex.large instances and 10 workers per instance:

| Configuration | Capacity |
|---|---|
| 1 instance (central) | 10 concurrent requests |
| 2 additional instances | 20 concurrent requests |
| **Total** | **30 concurrent requests** |
| Global RPM limit | 120 requests/minute |
| Per-domain RPM limit | 6 requests/minute |

### Adding More Instances

To add more worker instances:

```bash
export INSTANCE_COUNT=3  # Or desired count
deploy/aws/provision-ec2-workers.sh

# After instances are running, deploy workers:
deploy/aws/deploy-worker-instances.sh i-new1 i-new2 i-new3
```

## Worker Deployment Modes

### Mode 1: Fresh Deployment

Deploy the latest code on new/existing instances:

```bash
deploy/aws/deploy-worker-instances.sh <instance-ids>
```

### Mode 2: Hot Deploy (Code-Only Changes)

If only .NET code changed (not Docker configuration):

```bash
# On the instance (via SSH)
cd /opt/argus
./deploy/run-local.sh --hot
```

This updates running containers without rebuilding images.

### Mode 3: Restart Existing Workers

Restart workers without pulling new code:

```bash
# On each instance (via SSH)
cd /opt/argus
docker compose -f deploy/docker-compose.yml restart worker-spider
```

## Monitoring and Troubleshooting

### Check Worker Health

```bash
# Count running workers per instance
ssh -i ~/.ssh/kp1.pem ubuntu@<ip> 'docker ps -f "label=com.docker.compose.service=worker-spider" -q | wc -l'

# View worker memory/CPU usage
ssh -i ~/.ssh/kp1.pem ubuntu@<ip> 'docker stats --no-stream worker-spider-*'

# Check lock status (expired/active)
curl http://${COMMAND_CENTER_URL}/api/http-request-queue

# Get per-domain request counts
curl http://${COMMAND_CENTER_URL}/api/ops/metrics
```

### Debug Worker Issues

```bash
# View recent worker logs
ssh -i ~/.ssh/kp1.pem ubuntu@<ip> 'cd /opt/argus && docker compose logs --tail=50 worker-spider | grep -i error'

# Check RabbitMQ connectivity
ssh -i ~/.ssh/kp1.pem ubuntu@<ip> 'docker exec argus-v2-rabbitmq-1 rabbitmq-diagnostics -q ping'

# Verify database connectivity
ssh -i ~/.ssh/kp1.pem ubuntu@<ip> 'docker exec argus-v2-postgres-1 pg_isready -U argus'
```

### Scaling Down

To remove worker instances:

```bash
# Stop workers on instance
ssh -i ~/.ssh/kp1.pem ubuntu@<ip> 'cd /opt/argus && docker compose -f deploy/docker-compose.yml down'

# Terminate the EC2 instance
aws ec2 terminate-instances --region us-east-1 --instance-ids i-0123456789abcdef0
```

## Performance Tuning

### Adjust Concurrency

Modify database settings:

```sql
UPDATE http_request_queue_settings 
SET max_concurrency = 20,              -- More aggressive (if instances have capacity)
    per_domain_requests_per_minute = 6 -- Keep domain throttling
WHERE id = 1;
```

### Increase Request Timeout

For slower targets:

```sql
UPDATE http_request_queue_settings 
SET request_timeout_seconds = 60  -- Default is 30
WHERE id = 1;
```

### Monitor Adaptive Concurrency

Workers track failure rates and automatically reduce concurrency:

- Failure rate >= 65%: Drop to 1 concurrent request
- Failure rate >= 40%: Drop to 80% of max
- Failure rate >= 20%: Drop to 80% of max
- Failure rate <= 5%: Increase to max + 15% (up to 1000)

View in logs:

```bash
docker compose logs worker-spider | grep -i "concurrency\|adaptive"
```

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│ argusV2 HTTP Request Processing Infrastructure          │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  HTTP Request Queue (Postgres)                               │
│  ├─ Per-domain rate limiting (SQL constraint)                │
│  ├─ Pessimistic row locking (locked_by, locked_until_utc)   │
│  └─ Retry mechanism (state transitions)                      │
│                                                               │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  Worker Instances (m7i-flex.large):                          │
│  ├─ Instance 1 (Central):  10x worker-spider containers     │
│  ├─ Instance 2:            10x worker-spider containers     │
│  └─ Instance 3:            10x worker-spider containers     │
│                                                               │
│  Each worker-spider container:                               │
│  ├─ Runs HttpRequestQueueWorker background service          │
│  ├─ Leases requests from queue with domain lock check       │
│  ├─ Reports success/failure for adaptive concurrency        │
│  └─ Respects PerDomainRequestsPerMinute limit               │
│                                                               │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  Shared Infrastructure:                                      │
│  ├─ RabbitMQ: Event messaging                                │
│  ├─ Redis: Caching & state                                   │
│  └─ Postgres: Queue + metadata storage                       │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

## References

- Domain locking implementation: [HttpRequestQueueWorker.cs](../../src/argusV2.Workers.Spider/HttpRequestQueueWorker.cs#L152)
- Concurrency management: [AdaptiveConcurrencyController.cs](../../src/argusV2.Workers.Spider/AdaptiveConcurrencyController.cs)
- HTTP queue settings: [HttpRequestQueueSettings.cs](../../src/argusV2.Domain/Entities/HttpRequestQueueSettings.cs)
- Queue schema: Database migrations in `Infrastructure/Data`
