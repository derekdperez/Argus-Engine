# Argus Engine Development Component Updater Patch

Generated from live GitHub `main` inspection on the current repository state.

## Added

- `src/ArgusEngine.CommandCenter/Components/Pages/Development.razor.css`
- `src/ArgusEngine.CommandCenter/Endpoints/ComponentUpdateEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Services/Updates/ComponentUpdateModels.cs`
- `src/ArgusEngine.CommandCenter/Services/Updates/ComponentUpdateService.cs`
- `src/ArgusEngine.CommandCenter/Services/Updates/ComponentUpdateServiceRegistration.cs`

## Modified

- `deploy/Dockerfile.web`
- `deploy/docker-compose.yml`
- `src/ArgusEngine.CommandCenter/Components/Pages/Development.razor`
- `src/ArgusEngine.CommandCenter/Endpoints/CommandCenterEndpointRegistration.cs`
- `src/ArgusEngine.CommandCenter/Startup/CommandCenterServiceRegistration.cs`

## Notes

The updater requires:
- Command Center container can run `git`.
- Command Center container can run Docker/Compose via the mounted Docker socket.
- Repository root is mounted at `/workspace`.
- Postgres connection string is configured so `component_update_logs` can be created and written.
