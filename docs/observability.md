# Argus Engine Observability

Argus Engine emits OpenTelemetry metrics and traces from the Command Center, Gatekeeper, workers, outbox dispatcher, HTTP queue worker, high-value finding pipeline, and data-retention jobs.

## Local stack

Run the application with the optional observability compose overlay:

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.observability.yml up -d --build
```

Open:

- Grafana: `http://localhost:3000` with `admin/admin` unless overridden
- Prometheus: `http://localhost:9090`
- OTLP gRPC receiver: `localhost:4317`
- OTLP HTTP receiver: `localhost:4318`

The dashboard file is provisioned from:

```text
deploy/observability/grafana/dashboards/argus-engine-overview.json
```

## Configuration

Applications export telemetry when this key is set:

```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": "http://otel-collector:4317"
  }
}
```

For deployed environments, set the same key using environment variables:

```bash
OpenTelemetry__OtlpEndpoint=http://otel-collector:4317
```

## Metrics included

- `argus_http_queue_depth`
- `argus_outbox_depth`
- `argus_findings_total_current`
- `argus_assets_total_current`
- `argus_http_requests_completed_total`
- `argus_http_fetch_duration_ms`
- `argus_asset_admission_decisions_total`
- `argus_worker_loop_duration_ms`
- `argus_active_worker_leases`
- `argus_data_retention_deleted_rows_total`
- `argus_data_retention_archived_rows_total`

Avoid adding raw URL, target domain, or full asset names as metric labels. Use traces for high-cardinality values.
