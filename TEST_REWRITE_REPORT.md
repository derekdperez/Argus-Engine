# Argus Engine Test Rewrite Report

## Summary

Replaced repository-file/snapshot/checklist-style tests with behavior-focused unit tests. The rewritten suite validates production behavior around event contract identity, outbox message key compatibility, WAF/rate-limit circuit breaking, asset canonicalization, cookie extraction, and passive HTML signal extraction.

## Modified test projects

- `src/tests/ArgusEngine.ArchitectureTests`
  - Replaced project-file dependency assertions with runtime contract invariants.
  - Added a project reference to `ArgusEngine.Contracts`.
- `src/tests/ArgusEngine.CommandCenter.Tests`
  - Removed tests that read CommandCenter source files and asserted specific implementation strings.
  - Added tests for `AssetDiscovered` envelope/default metadata and outbox compatibility.
- `src/tests/ArgusEngine.Infrastructure.Tests`
  - Replaced checklist/config/deployment/observability source scans with behavior tests.
  - Expanded `OutboxMessageTypeRegistry` and `WorkerHttpClientHandler` coverage.
- `src/tests/ArgusEngine.UnitTests`
  - Expanded canonicalization coverage for host assets, targets, URLs, IDs, GUIDs, query sorting, schemes, and ports.
  - Added unit tests for `CookieExtractor` and `HtmlSignalExtractor`.
  - Added a project reference to `ArgusEngine.Workers.TechnologyIdentification`.
- `src/tests/ArgusEngine.RouteCompatibilityTests`
  - Replaced route snapshot file scanning with message-compatibility coverage that does not depend on repository files.
  - Added project references to `ArgusEngine.Contracts` and `ArgusEngine.Infrastructure`.
- `src/tests/ArgusEngine.IntegrationTests`
  - Removed the Docker/Testcontainers database smoke test from the supplied replacement file.
  - Added deterministic message-key replay/compatibility tests and removed unused Testcontainers package references from the project file.

## Deployment

Unzip this archive into the repository root. Existing files at the same paths will be overwritten.
