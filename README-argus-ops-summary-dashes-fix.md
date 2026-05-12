# Argus Operations summary-card loading fix

This patch removes the visible `Loading...` text from the Operations summary cards and restores dash placeholders while the first target/asset snapshot is still loading.

Affected repo file:

```text
src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor
```

Why this is a script instead of a full file overlay:

- The current GitHub `OpsRadzen.razor` file is large and mostly compressed into very long lines.
- This patch changes only the first summary-card branch and avoids overwriting the rest of the file.

Apply from the repository root:

```bash
python3 scripts/apply-ops-summary-dashes.py
```

Then rebuild/recreate the web app:

```bash
dotnet build ArgusEngine.slnx
docker compose -f deploy/docker-compose.yml build command-center-web
docker compose -f deploy/docker-compose.yml up -d --force-recreate command-center-web
```

Expected behavior:

- During the initial Operations page load, the summary card row shows:
  `Targets — Assets — Subdomains — Unique Technologies — HTTP Queue — Asset Storage —`
- It no longer shows `Loading...` in the summary cards.
- Existing section-level loading text remains unchanged for Targets, Assets, and HTTP Requests.
