# Argus ECS Command Center Web Bootstrap

This overlay adds a one-command bootstrap for the missing ECS web service.

## Files

```text
deploy/aws/bootstrap-ecs-command-center-web.sh
deploy/aws/ecs-command-center-status.sh
```

## What the bootstrap script creates

- ECR repository: `argus-v2/command-center`
- ECS cluster: `argus-v2`
- ECS task execution role and task role
- CloudWatch Logs group: `/ecs/argus-v2`
- Public Application Load Balancer
- ALB target group for container port `8080`
- ECS Fargate service: `argus-command-center`

The script assumes the Command Center app is the web UI and uses:

```text
deploy/Dockerfile.web
src/ArgusEngine.CommandCenter
container port 8080
health path /health/ready
```

## Recommended first run

Run this from the repo root on the EC2 machine where your current Docker Compose stack already runs Postgres, Redis, and RabbitMQ:

```bash
chmod +x deploy/aws/bootstrap-ecs-command-center-web.sh
chmod +x deploy/aws/ecs-command-center-status.sh

AWS_REGION=us-east-1 \
AWS_ACCOUNT_ID=415144299522 \
WAIT_FOR_STABLE=0 \
deploy/aws/bootstrap-ecs-command-center-web.sh
```

The script will detect the EC2 private IP and configure the ECS task to connect back to that host for:

```text
Postgres: 5432
Redis: 6379
RabbitMQ: 5672
RabbitMQ management: 15672
```

It will also add inbound security group rules from the ECS task security group to the EC2 instance security group for those ports.

## If the database/broker credentials are not the defaults

Pass them as environment variables:

```bash
AWS_REGION=us-east-1 \
AWS_ACCOUNT_ID=415144299522 \
ARGUS_DB_USERNAME='your-user' \
ARGUS_DB_PASSWORD='your-password' \
ARGUS_RABBITMQ_USERNAME='your-rabbit-user' \
ARGUS_RABBITMQ_PASSWORD='your-rabbit-password' \
deploy/aws/bootstrap-ecs-command-center-web.sh
```

## If you are not running the script on EC2

Set network and backend values manually:

```bash
AWS_REGION=us-east-1 \
AWS_ACCOUNT_ID=415144299522 \
VPC_ID=vpc-xxxxxxxx \
ALB_SUBNETS=subnet-aaa,subnet-bbb \
ECS_SUBNETS=subnet-aaa,subnet-bbb \
CORE_HOST=10.0.1.25 \
deploy/aws/bootstrap-ecs-command-center-web.sh
```

## Restrict the public web UI to your IP

By default the ALB allows port 80 from `0.0.0.0/0`.

To restrict it:

```bash
ALB_ALLOWED_CIDR='203.0.113.10/32' \
deploy/aws/bootstrap-ecs-command-center-web.sh
```

## Check status

```bash
AWS_REGION=us-east-1 deploy/aws/ecs-command-center-status.sh
```

## Notes

This is an MVP bootstrap. It stores connection strings directly in the ECS task definition environment so you can get running quickly. For production hardening, move passwords into AWS Secrets Manager and reference them from the task definition `secrets` block.
