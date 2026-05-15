# CommandCenter Split Cutover

The Command Center has been cut over to split services. The gateway routes directly to the owning services; there is no legacy monolith fallback.

## Final Shape

- Replaces the Gateway legacy catch-all with explicit split-service routing.
- Removes the Gateway's implicit `legacy-command-center` fallback behavior.
- Adds `/api/gateway/routes` diagnostics.
- Adds WebSocket forwarding for `/hubs/discovery`.
- Makes `deploy.py` split-first and points local smoke checks at the Gateway on port `8081`.
- Ports discovery, maintenance/admin, operations, worker-control, updates, realtime, and web routes into their owning split hosts.
- Moves realtime UI notifications onto the split realtime host via RabbitMQ `LiveUiEventDto` consumption.
- Replaces direct Web database access with API clients through the gateway.
- Adds a route ownership manifest under `CommandCenter.Contracts`.
- Updates route compatibility tests to snapshot the split route surface.

## Verification

Run `dotnet test src/tests/ArgusEngine.RouteCompatibilityTests/ArgusEngine.RouteCompatibilityTests.csproj` to verify route ownership and route-surface compatibility.
