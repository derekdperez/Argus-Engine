# CommandCenter Refactor Cutover Patch

This zip contains only modified or added files. It is intended to be extracted at the repository root.

## What this patch changes

- Replaces the Gateway legacy catch-all with explicit split-service routing.
- Removes the Gateway's implicit `legacy-command-center` fallback behavior.
- Adds `/api/gateway/routes` diagnostics.
- Adds WebSocket forwarding for `/hubs/discovery`.
- Makes `deploy-local.sh` split-first and points local smoke checks at the Gateway on port `8081`.
- Fixes the invalid `AddSingleton()` call in `CommandCenter.Maintenance.Api`.
- Replaces fake "Accepted" behavior in WorkerControl restart with a truthful `501 Not Implemented` until real restart orchestration is ported.
- Adds owner-aware `501 Not Implemented` responses for split APIs whose routes are owned but not yet ported.
- Adds a route ownership manifest under `CommandCenter.Contracts`.
- Adds a focused split smoke script.

## Important limitation

This patch makes the refactor honest and deployable as a split stack, but it does not magically port all legacy feature logic. Routes that are still not implemented now fail fast with owner diagnostics instead of silently proxying to the legacy monolith or returning fake success.

The next implementation step is to move each legacy endpoint body into its owning split API, keeping the same request/response contract and adding Gateway parity tests.
