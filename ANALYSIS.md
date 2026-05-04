# Analysis Summary

The web application disappeared in commit `803fbd2`, which has one parent, `717c1c5`, and changed 367 files with 1,703 additions and 25,005 deletions.

The deleted web surface was not limited to Razor pages. The deleted `src/NightmareV2.CommandCenter` tree included:

- Razor app shell: `App.razor`, `Routes.razor`, `_Imports.razor`
- Layout and reconnect modal files
- Custom data grid components
- Pages: `Admin`, `AssetGraph`, `AssetTreeView`, `Error`, `HighValueFindings`, `Home`, `NotFound`, `OpsRadzen`, `Status`, `Targets`, `Technologies`
- Static assets: `app.css`, `favicon.ico`, `nightmare-ui.js`
- UI/API endpoints: admin usage, asset graph, bus journal, event trace, file store, high-value findings, HTTP queue, ops, tags, targets, tool restart, workers, EC2 worker scaling
- Models, SignalR hub DTOs, realtime consumers/client
- Diagnostics/data-maintenance endpoints
- Sensitive endpoint protection
- AWS/ECS worker scaling services
- Root spider seed service and worker scale definition provider
- Runtime/startup helpers

Current `main` has partial replacements. Some should be preserved because they are post-deletion functionality:
`AssetAdmission`, data retention admin, HTTP artifact backfill, liveness/readiness checks, and static asset endpoint routing.

This overlay restores the deleted web app into `src/ArgusEngine.CommandCenter` while preserving those current pieces and removing only the route-conflicting `Operations.razor` replacement.
