# Actionable observability

Argus Engine already emits traces and metrics. The next step is to make operational signals answer two questions quickly:

1. Is the platform healthy enough to run reconnaissance safely?
2. Where should an operator look first when it is not?

## Operational status snapshot

The Command Center exposes a machine-readable snapshot at:

```text
GET /api/status/summary
```

The snapshot contains:

- Component version/build state
- Worker desired/running status
- Queue depth and oldest queue age
- Dependency readiness
- SLO indicators
- Active alerts

The `/status` page consumes the same API and subscribes to SignalR updates.

## Recommended SLOs

| Area | Healthy | Degraded | Critical |
| --- | ---: | ---: | ---: |
| HTTP queue oldest age | under 5 minutes | 5–30 minutes | over 30 minutes |
| HTTP queue depth | under 1,000 | 1,000–9,999 | 10,000+ |
| Outbox depth | under 100 | 100–999 | 1,000+ |
| Worker availability | desired workers active | desired workers unknown | required worker disabled |
| Postgres connectivity | connected | n/a | unavailable |

## Metrics added by this patch

| Metric | Type | Purpose |
| --- | --- | --- |
| `argus_http_queue_oldest_age_seconds` | histogram | Age of the oldest pending HTTP queue item |
| `argus_http_queue_depth` | up/down counter | Observed HTTP queue depth |
| `argus_outbox_depth` | up/down counter | Observed outbox backlog |
| `argus_worker_desired_count` | up/down counter | Desired worker count by worker key |
| `argus_worker_running_count` | up/down counter | Running or effective worker count by worker key |
| `argus_dependency_health` | up/down counter | Dependency health by dependency name |
| `argus_realtime_ui_events_total` | counter | UI events published through SignalR |
| `argus_config_alias_accesses_total` | counter | Legacy/current configuration alias usage |
| `argus_operational_alerts_total` | counter | Operational alert count by severity |

## Dashboard panels

Create panels for:

- Queue depth by queue
- Oldest queue age
- Outbox backlog
- Worker desired vs running/effective counts
- Dependency health
- Operational alerts by severity
- Legacy configuration alias usage
- HTTP request completion rate and failure rate
- Asset admission accepts/rejects by reason

## Alert rules

Start with these conservative thresholds:

- Postgres unavailable for 1 minute.
- HTTP queue oldest age over 30 minutes.
- HTTP queue depth over 10,000.
- Outbox depth over 1,000.
- Any required worker disabled.
- Legacy configuration alias usage appears in production after migration freeze.
- No realtime UI events for a prolonged period while queue depth is non-zero.

## Runbook pointers

When an alert fires:

1. Open `/status` first.
2. Check Postgres and RabbitMQ health before restarting workers.
3. Check HTTP queue age before queue depth; age is a better user-impact signal.
4. Check outbox backlog if UI events or downstream work are delayed.
5. Check worker toggles and scaling settings before changing ECS/EC2 manually.
6. Check legacy configuration alias metrics if behavior differs between environments.
