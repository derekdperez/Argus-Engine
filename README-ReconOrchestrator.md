# ReconOrchestrator overlay

This overlay adds a new `ArgusEngine.Workers.Orchestration` worker project and the first orchestrator implementation: `ReconOrchestrator`.

## What it does

- Maintains one active orchestrator lease per target using `recon_orchestrator_states`.
- Persists the full serialized orchestrator state after every reconciliation tick.
- Records and requests `subfinder` and `amass` subdomain enumeration via the existing `SubdomainEnumerationRequested` contract.
- Tracks subdomain spider status:
  - `NotStarted` when no URL asset exists for the subdomain.
  - `Resumable` when URL assets exist but one or more are not confirmed/complete.
  - `Complete` when all known URL assets for the subdomain are confirmed/complete.
- Publishes seed URL admissions for new subdomains and indexed resume events for pending URL assets.
- Generates deterministic browser/header profiles from `(target, subdomain, machine identity)` so the same machine/IP uses the same realistic headers for the same subdomain.
- Stores profile assignments in `recon_orchestrator_profile_assignments`.

## Apply

Unzip this archive at the repository root, then run one of:

```bash
chmod +x scripts/apply-recon-orchestrator-overlay.sh
./scripts/apply-recon-orchestrator-overlay.sh
```

or:

```powershell
.\scripts\Apply-ReconOrchestratorOverlay.ps1
```

The script only adds the project to `ArgusEngine.slnx`. The overlay already places the new project and updates `WorkerKeys`.

## Configuration

A sample file is included at:

```text
src/ArgusEngine.Workers.Orchestration/appsettings.recon-orchestrator.sample.json
```

Defaults match the requested first version:

- `ReconProfilesPerTarget = 8`
- `ReconProfilesPerSubdomain = 2`
- `RequestsPerMinutePerSubdomain = 120`
- `RandomDelayMin = 0.25`
- `RandomDelayMax = 2.5`
- `RandomDelayEnabled = true`
- `RandomizeHeaderOrderEnabled = true`
- device types: `mobile`, `desktop`, `tablet`
- browsers: `firefox`, `chrome`, `safari`
- OS: `windows`, `ios`, `android`, `chrome`
- `ReconProfileHardwareAge = 12`

## Build

```bash
dotnet build src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj
```

## Notes

This implementation publishes only existing Argus contracts. It stores rich profile/header configuration in durable tables and event relationship metadata; HTTP requester/spider worker changes can later read `recon_orchestrator_profile_assignments` directly to enforce those headers at request time.
