# Command Center performance overlay

This overlay contains only files that were added or replaced for the Command Center performance pass.

## What changed

- `src/ArgusEngine.CommandCenter.Web/Program.cs`
  - Enables ASP.NET Core response compression.
  - Adds lightweight `Server-Timing` and `X-Argus-App-Duration-Ms` headers for per-request app timing.
  - Keeps the existing gateway base-address resolution and static asset cache behavior.

- `src/ArgusEngine.CommandCenter.Web/Components/Pages/CommandCenter.razor`
  - Replaces the heavy `/commandcenter` workspace with a bounded, lazy-loading workspace.
  - Removes the previous full-table background hydration behavior.
  - Keeps `/` and `/ops` owned by the existing OpsRadzen page to avoid duplicate route ownership.
  - Uses virtualized rows for loaded datasets.
  - Loads high-value and technology panels only when the user opens those tabs.
  - Refreshes only the active tab instead of reloading every panel.
  - Adds optimistic/non-blocking target and HTTP queue actions.

- `src/ArgusEngine.CommandCenter.Web/Components/Pages/CommandCenter.razor.css`
  - Adds isolated styles for the new faster Command Center page.

- `src/ArgusEngine.CommandCenter.Discovery.Api/Sql/command-center-performance-indexes.sql`
  - Adds PostgreSQL index recommendations for the largest Command Center read paths.
  - This file is not executed automatically. Run it manually with `psql` because `CREATE INDEX CONCURRENTLY` cannot run inside a transaction block.

## Apply

From the repository root:

```bash
unzip argus-command-center-performance-overlay.zip
dotnet build
```

Then deploy using your normal pipeline.

## Database indexes

Run the SQL manually after confirming table/column names in your database:

```bash
psql "$ARGUS_POSTGRES_CONNECTION" -f src/ArgusEngine.CommandCenter.Discovery.Api/Sql/command-center-performance-indexes.sql
```

## Notes

This overlay intentionally avoids changing discovery API DTO contracts because the existing endpoints were only available for remote inspection. The fastest next step after this overlay is to add true server-side `/grid` endpoints for assets, HTTP queue, high-value findings, and technology observations, then wire the Blazor `Virtualize` components to `ItemsProvider` callbacks.
