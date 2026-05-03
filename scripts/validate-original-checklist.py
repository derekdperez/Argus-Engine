#!/usr/bin/env python3
"""Validate source-level completion signals for the original Argus checklist."""

from __future__ import annotations

from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_VERSION = "2.4.0"
EXPECTED_FILE_VERSION = "2.4.0.0"

failures: list[str] = []

def require(path: str) -> Path:
    p = ROOT / path
    if not p.exists():
        failures.append(f"missing {path}")
    return p

def require_contains(path: str, *needles: str) -> None:
    p = require(path)
    if not p.exists():
        return
    text = p.read_text(encoding="utf-8", errors="ignore")
    for needle in needles:
        if needle not in text:
            failures.append(f"{path} does not contain {needle!r}")

def require_not_contains(path: str, *needles: str) -> None:
    p = require(path)
    if not p.exists():
        return
    text = p.read_text(encoding="utf-8", errors="ignore")
    for needle in needles:
        if needle in text:
            failures.append(f"{path} still contains {needle!r}")

def validate_versioning() -> None:
    require_contains("Directory.Build.targets", EXPECTED_VERSION, EXPECTED_FILE_VERSION, "ValidateArgusEngineDeploymentVersion")
    require_contains("VERSION", EXPECTED_VERSION)
    require_contains("deploy/docker-compose.yml", f"${{ARGUS_ENGINE_VERSION:-{EXPECTED_VERSION}}}")

def validate_observability_stack() -> None:
    require_contains("deploy/docker-compose.observability.yml", "otel/opentelemetry-collector-contrib", "prom/prometheus", "grafana/grafana")
    require_contains("deploy/observability/otel-collector-config.yml", "otlp:", "prometheus:", "0.0.0.0:9464")
    require_contains("deploy/observability/prometheus.yml", "otel-collector:9464")
    dash = require("deploy/observability/grafana/dashboards/argus-engine-overview.json")
    if dash.exists():
        payload = json.loads(dash.read_text(encoding="utf-8"))
        if payload.get("uid") != "argus-engine-overview":
            failures.append("Grafana dashboard uid is not argus-engine-overview")

def validate_original_acceptance_files() -> None:
    require_contains("src/NightmareV2.CommandCenter/Components/Pages/AssetAdmission.razor", "/asset-admission", "asset-admission-decisions")
    require_contains("src/NightmareV2.Infrastructure/Messaging/OutboxDispatcherWorker.cs", "ArgusMeters.OutboxDispatched", "ArgusMeters.OutboxDeadLettered", "outbox.dispatch")
    require_contains("src/NightmareV2.Infrastructure/DataRetention/DataRetentionWorker.cs", "ArgusMeters.DataRetentionDeletedRows", "ArgusMeters.DataRetentionArchivedRows")
    require_contains("src/NightmareV2.Infrastructure/Observability/ArgusObservabilityExtensions.cs", "AddOpenTelemetry", "AddOtlpExporter")
    require_contains("src/NightmareV2.Infrastructure/Observability/ArgusMetrics.cs", "argus_http_queue_depth", "argus_outbox_depth")
    require_contains("src/NightmareV2.Application/Gatekeeping/GatekeeperOrchestrator.cs", "IAssetAdmissionDecisionWriter", "AssetAdmissionDecisionKind.Accepted")
    require_contains("src/NightmareV2.Workers.Spider/HttpRequestQueueWorker.cs", "IHttpArtifactStore", "ResponseBodyBlobId")
    require_contains("src/NightmareV2.Workers.HighValue/Consumers/HighValueRegexConsumer.cs", "IHttpArtifactReader", "MaxResponseBodyScanBytes")
    require_contains("src/NightmareV2.Workers.TechnologyIdentification/Consumers/TechnologyIdentificationConsumer.cs", "IHttpArtifactReader", "MaxResponseBodyScanBytes")

def validate_rename_artifacts() -> None:
    require_contains("scripts/apply-original-checklist-refactor.py", "PROJECT_RENAMES", "ArgusEngine.slnx", "PRESERVE_TOKENS")
    require_contains("ArgusEngine.slnx", "ArgusEngine.CommandCenter", "ArgusEngine.Infrastructure")

def main() -> int:
    validate_versioning()
    validate_observability_stack()
    validate_original_acceptance_files()
    validate_rename_artifacts()

    if failures:
        print("Validation failed:")
        for failure in failures:
            print(f" - {failure}")
        return 1

    print("Original checklist source validation passed.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
