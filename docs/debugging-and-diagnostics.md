# Nightmare v2 Debugging and Diagnostics Guide

This guide describes the debugging helpers added for local development and deployment troubleshooting.

## What changed

The update adds:

- `deploy/dev-check.sh` — one command that validates Compose, builds images, starts the stack, runs smoke tests, and prints error-like log lines.
- `deploy/smoke-test.sh` — checks the Command Center health endpoints, Blazor static assets, and diagnostics APIs.
- `deploy/logs.sh` — focused Compose log helper with error highlighting and service filtering.
- `GET /api/diagnostics/dependencies` — authenticated dependency diagnostics for Postgres, file-store Postgres, Redis, RabbitMQ TCP reachability, and expected static asset paths.
- Ready health checks now use `/health/ready` instead of `/health` so Docker reports Command Center as healthy only after the database is reachable.

## Recommended daily workflow

Start with the full sanity check:

```bash
./deploy/dev-check.sh
```

Use a clean rebuild when Docker cache or dependency state is suspicious:

```bash
./deploy/dev-check.sh --fresh
```

Skip builds when you only want to re-check the running stack:

```bash
./deploy/dev-check.sh --no-build
```

## Smoke-test only

After a deploy or restart:

```bash
./deploy/smoke-test.sh
```

For a remote host:

```bash
BASE_URL=http://YOUR_HOST:8080 ./deploy/smoke-test.sh
```

If you changed the diagnostics key:

```bash
NIGHTMARE_DIAGNOSTICS_API_KEY='your-key' ./deploy/smoke-test.sh
```

The script verifies:

1. `/health`
2. `/health/ready`
3. `/_framework/blazor.web.js`
4. `/app.css`
5. `/api/diagnostics/self`
6. `/api/diagnostics/dependencies`

## Logs

Show status, recent logs, and highlighted failures:

```bash
./deploy/logs.sh
```

Show only error-like lines:

```bash
./deploy/logs.sh --errors
```

Follow selected services:

```bash
./deploy/logs.sh --follow command-center worker-spider
```

Increase the tail window:

```bash
TAIL=500 ./deploy/logs.sh --errors
```

## Diagnostics endpoints

Diagnostics are disabled unless configured.

In Docker Compose, the Command Center already enables diagnostics with:

```yaml
Nightmare__Diagnostics__Enabled: "true"
Nightmare__Diagnostics__ApiKey: ${NIGHTMARE_DIAGNOSTICS_API_KEY:-local-dev-diagnostics-key-change-me}
```

Call diagnostics manually:

```bash
curl -fsS \
  -H 'X-Nightmare-Diagnostics-Key: local-dev-diagnostics-key-change-me' \
  http://localhost:8080/api/diagnostics/self

curl -fsS \
  -H 'X-Nightmare-Diagnostics-Key: local-dev-diagnostics-key-change-me' \
  http://localhost:8080/api/diagnostics/dependencies
```

Expected dependency diagnostics shape:

```json
{
  "service": "command-center",
  "overall": "ok",
  "checks": {
    "postgres": { "status": "ok" },
    "fileStore": { "status": "ok" },
    "redis": { "status": "ok" },
    "rabbitMqTcp": { "status": "ok" },
    "staticAssets": { "status": "ok" }
  }
}
```

## How to debug by failure stage

### Build-time failures

Run:

```bash
COMPOSE_BAKE=false docker compose -f deploy/docker-compose.yml build worker-enum
```

Use this when failures mention `go install`, NuGet restore, Dockerfile stages, or missing files during image creation.

### Startup failures

Run one service at a time:

```bash
docker compose -f deploy/docker-compose.yml up command-center
docker compose -f deploy/docker-compose.yml up worker-spider
```

Then inspect:

```bash
./deploy/logs.sh --errors command-center worker-spider
```

### Dependency failures

Run:

```bash
./deploy/smoke-test.sh
```

Then manually inspect dependency diagnostics if needed:

```bash
curl -fsS \
  -H "X-Nightmare-Diagnostics-Key: ${NIGHTMARE_DIAGNOSTICS_API_KEY:-local-dev-diagnostics-key-change-me}" \
  http://localhost:8080/api/diagnostics/dependencies
```

### Frontend/static asset failures

The smoke test explicitly checks:

```text
/_framework/blazor.web.js
/app.css
```

If either fails, debug the Command Center image and `App.razor` asset paths first.

## Interpreting common results

| Symptom | Most likely area | First command |
|---|---|---|
| Docker build exits before containers start | Dockerfile or external tool version | `docker compose -f deploy/docker-compose.yml build SERVICE` |
| Container exits immediately | Runtime config or startup exception | `./deploy/logs.sh --errors SERVICE` |
| `/health` passes but `/health/ready` fails | Database dependency | `./deploy/smoke-test.sh` |
| `blazor.web.js` returns 404 | Blazor static asset publishing/pathing | `curl -i http://localhost:8080/_framework/blazor.web.js` |
| Workers run but no work progresses | RabbitMQ, outbox, or worker toggles | `./deploy/logs.sh --errors gatekeeper worker-enum worker-spider` |

## Security note

Do not expose diagnostics publicly with the default key. For public deployments, set a strong key:

```bash
export NIGHTMARE_DIAGNOSTICS_API_KEY='replace-with-a-long-random-secret'
./deploy/deploy.sh
```

Or disable diagnostics in production by setting:

```yaml
Nightmare__Diagnostics__Enabled: "false"
```
