# Argus Command Center Web App Restore Overlay

This package is designed for the public repo:

`https://github.com/derekdperez/Argus-Engine`

It restores the deleted Command Center web application files from the commit immediately before the destructive deletion/refactor:

- Source commit before deletion: `717c1c568b38bb4fc84c9b34c54e90ed362d2ffb`
- Deletion/refactor commit: `803fbd2`

## What this package does

`Apply-ArgusCommandCenterWebRestore.ps1` downloads the deleted files from the SHA-pinned GitHub raw URLs, restores them into the current `src/ArgusEngine.CommandCenter` tree, and applies only the minimal compatibility edits needed for the current repo:

1. `NightmareV2` namespace/project prefix is changed to `ArgusEngine`.
2. `AddNightmareRabbitMq` is changed to `AddArgusRabbitMq`.
3. `nightmare-ui.js` references are changed to `argus-ui.js`.
4. Current post-deletion endpoints are preserved:
   - `AssetAdmissionDecisionEndpoints`
   - `DataRetentionAdminEndpoints`
   - `HttpArtifactBackfillEndpoints`
5. Current web health/static-asset middleware behavior is preserved.
6. The post-deletion replacement `Operations.razor` page is removed to avoid route conflicts with restored `OpsRadzen.razor`.

## Usage

From the repo root:

```powershell
Expand-Archive .\argus-command-center-web-restore-overlay.zip -DestinationPath .\argus-restore
.\argus-restore\Apply-ArgusCommandCenterWebRestore.ps1 -RepoRoot .
dotnet build ArgusEngine.slnx
git diff -- src/ArgusEngine.CommandCenter
```

For a dry run:

```powershell
.\argus-restore\Apply-ArgusCommandCenterWebRestore.ps1 -RepoRoot . -DryRun
```

By default, the script creates a backup folder:

`.argus-web-restore-backup/<timestamp>/`

## Deliberately preserved current files

These are not overwritten by default because they represent post-deletion functionality that does not conflict with restoring the old web app:

- `src/ArgusEngine.CommandCenter/Components/Pages/AssetAdmission.razor`
- `src/ArgusEngine.CommandCenter/Endpoints/AssetAdmissionDecisionEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/DataRetentionAdminEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/HttpArtifactBackfillEndpoints.cs`
- `src/ArgusEngine.CommandCenter/DataMaintenance/HttpQueueArtifactBackfillService.cs`
- current `appsettings*.json`
- current `StartupDatabaseInitializer.cs`

These three endpoint files also existed at `717c1c5`, but are preserved by default because they exist in current `main` and may contain necessary post-deletion compatibility changes:

- `src/ArgusEngine.CommandCenter/Endpoints/AssetAdmissionDecisionEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/DataRetentionAdminEndpoints.cs`
- `src/ArgusEngine.CommandCenter/Endpoints/HttpArtifactBackfillEndpoints.cs`

To force those three files back to their exact `717c1c5` versions, run:

```powershell
.\argus-restore\Apply-ArgusCommandCenterWebRestore.ps1 -RepoRoot . -RestoreOldPostDeletionEndpointVersions
```

## Files removed by the delete script

`Remove-PostDeletionWebReplacements.ps1` removes only:

- `src/ArgusEngine.CommandCenter/Components/Pages/Operations.razor`
- `src/ArgusEngine.CommandCenter/Components/Pages/Operations.razor.css`

Those files are replacement/stub pages created after the deletion and conflict with the restored `OpsRadzen.razor` routes.

## Important limitation

This package is an overlay/restoration helper, not a fully compiled patch generated from a local clone. The execution environment could browse GitHub but could not clone/download the repository directly, so the actual old source files are fetched by the PowerShell script from SHA-pinned GitHub raw URLs when you run it.
