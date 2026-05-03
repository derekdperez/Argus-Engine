# Argus Engine Refactor / Hardening Checklist

Source: uploaded implementation plan.

## Completed in this overlay

- [x] Central deployment version bumped to `2.2.0` / `2.2.0.0`.
- [x] All .NET projects inherit the same forced deployment version through `Directory.Build.targets`.
- [x] Docker image `COMPONENT_VERSION` defaults bumped from `2.0.0` to `2.2.0`.
- [x] Docker OCI labels now use the same version passed into `dotnet publish`.
- [x] Docker Compose image names moved to `argus-engine/*` and tag with `${ARGUS_ENGINE_VERSION:-2.2.0}`.
- [x] Compose supplies both `Argus__*` and temporary `Nightmare__*` compatibility variables.
- [x] Added shell and PowerShell deployment-version verification scripts.
- [x] Added automated test coverage for central versioning, Dockerfile labels, and compose image/tag defaults.

## Already present in current GitHub main before this overlay

- [x] Gatekeeper admission decision writer files exist.
- [x] Gatekeeper orchestrator writes admission decisions on accept/drop/error paths.
- [x] URL fetch snapshot contains blob reference metadata.
- [x] Command Center has endpoint/startup split scaffolding.
- [x] Command Center Docker status screen reads Docker image version labels/tags.

## Still not performed by unzip-only overlay

- [ ] Full physical solution/project directory rename from `NightmareV2.*` to `ArgusEngine.*`.
- [ ] Full namespace rename to `ArgusEngine.*`.
- [ ] Verified `dotnet build` and `dotnet test` in a networked/dev environment.
- [ ] Verified Docker Compose build/run on a host with Docker available.
