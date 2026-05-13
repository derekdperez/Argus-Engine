#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "[argus fix] Applying Ops counts, maintenance endpoint, and autoscaler path fixes from: $repo_root"

for f in \
  "src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor" \
  "src/ArgusEngine.CommandCenter.Maintenance.Api/Program.cs" \
  "src/ArgusEngine.CommandCenter.Maintenance.Api/Endpoints/HttpArtifactBackfillEndpoints.cs" \
  "src/ArgusEngine.CommandCenter.WorkerControl.Api/Services/DockerComposeWorkerScaler.cs"
do
  if [ ! -f "$f" ]; then
    echo "[argus fix] Missing expected file: $f" >&2
    exit 1
  fi
done

python3 - <<'PY'
from pathlib import Path
import re
import sys

ops = Path("src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor")
text = ops.read_text()
text2 = text
text2 = re.sub(
    r'private\s+long\s+TotalTargets\s*=>\s*_targets\.Count\s*;',
    'private long TotalTargets => OverviewLong("totalTargets", "TotalTargets");',
    text2,
    count=1,
)
text2 = re.sub(
    r'private\s+long\s+ConfirmedAssets\s*=>\s*_targets\.Sum\(t\s*=>\s*Long\(t,\s*"confirmedAssetCount",\s*"ConfirmedAssetCount"\)\)\s*;',
    'private long ConfirmedAssets => OverviewLong("totalAssetsConfirmed", "TotalAssetsConfirmed", "confirmedAssets", "ConfirmedAssets");',
    text2,
    count=1,
)
text2 = re.sub(
    r'private\s+long\s+ConfirmedSubdomains\s*=>\s*_targets\.Sum\(t\s*=>\s*Long\(t,\s*"subdomainCount",\s*"SubdomainCount"\)\)\s*;',
    'private long ConfirmedSubdomains => OverviewLong("subdomainsConfirmed", "SubdomainsConfirmed", "confirmedSubdomains", "ConfirmedSubdomains");',
    text2,
    count=1,
)

if text2 != text:
    ops.write_text(text2)
    print("[argus fix] Updated OpsRadzen top summary totals to use /api/ops/overview.")
else:
    print("[argus fix] OpsRadzen summary totals already fixed or file has diverged.")

program = Path("src/ArgusEngine.CommandCenter.Maintenance.Api/Program.cs")
p = program.read_text()
if "builder.Services.AddScoped<HttpQueueArtifactBackfillService>();" not in p:
    marker = "builder.Services.AddScoped<WorkerCancellationStore>();"
    if marker in p:
        p = p.replace(marker, marker + "\n\nbuilder.Services.AddScoped<HttpQueueArtifactBackfillService>();", 1)
    else:
        marker = "builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });"
        if marker not in p:
            print("[argus fix] Could not find Maintenance API service registration insertion point.", file=sys.stderr)
            sys.exit(1)
        p = p.replace(marker, marker + "\n\nbuilder.Services.AddScoped<HttpQueueArtifactBackfillService>();", 1)
    program.write_text(p)
    print("[argus fix] Registered HttpQueueArtifactBackfillService in Maintenance API.")
else:
    print("[argus fix] Maintenance API service registration already present.")

endpoint = Path("src/ArgusEngine.CommandCenter.Maintenance.Api/Endpoints/HttpArtifactBackfillEndpoints.cs")
e = endpoint.read_text()
if "[FromServices] HttpQueueArtifactBackfillService service" not in e:
    if "HttpQueueArtifactBackfillService service" not in e:
        print("[argus fix] Could not find HttpQueueArtifactBackfillService endpoint parameter.", file=sys.stderr)
        sys.exit(1)
    e = e.replace("HttpQueueArtifactBackfillService service", "[FromServices] HttpQueueArtifactBackfillService service", 1)
    endpoint.write_text(e)
    print("[argus fix] Marked HttpQueueArtifactBackfillService parameter as [FromServices].")
else:
    print("[argus fix] Http artifact backfill endpoint already uses [FromServices].")

scaler = Path("src/ArgusEngine.CommandCenter.WorkerControl.Api/Services/DockerComposeWorkerScaler.cs")
s = scaler.read_text()
if "NormalizeComposeRepoPath(" not in s:
    replaced = False
    patterns = [
        (r'var\s+repoRoot\s*=\s*configuration\["ARGUS_REPO_ROOT"\]\s*\?\?\s*"/workspace"\s*;',
         'var repoRoot = NormalizeComposeRepoPath(configuration["ARGUS_REPO_ROOT"] ?? "/workspace");'),
        (r'var\s+repoRoot\s*=\s*configuration\["ARGUS_REPO_ROOT"\]\s*\?\?\s*Directory\.GetCurrentDirectory\(\)\s*;',
         'var repoRoot = NormalizeComposeRepoPath(configuration["ARGUS_REPO_ROOT"] ?? Directory.GetCurrentDirectory());'),
        (r'var\s+repoRoot\s*=\s*configuration\.GetValue<string>\("ARGUS_REPO_ROOT"\)\s*\?\?\s*"/workspace"\s*;',
         'var repoRoot = NormalizeComposeRepoPath(configuration.GetValue<string>("ARGUS_REPO_ROOT") ?? "/workspace");'),
        (r'var\s+repoRoot\s*=\s*configuration\.GetValue<string>\("ARGUS_REPO_ROOT"\)\s*\?\?\s*Directory\.GetCurrentDirectory\(\)\s*;',
         'var repoRoot = NormalizeComposeRepoPath(configuration.GetValue<string>("ARGUS_REPO_ROOT") ?? Directory.GetCurrentDirectory());'),
    ]
    for pat, repl in patterns:
        s2, n = re.subn(pat, repl, s, count=1)
        if n:
            s = s2
            replaced = True
            break

    if not replaced:
        # This fallback intentionally targets only the first repoRoot local declaration.
        s2, n = re.subn(r'(var\s+repoRoot\s*=\s*)([^;]+);', r'\1NormalizeComposeRepoPath(\2);', s, count=1)
        if n:
            s = s2
            replaced = True

    if replaced:
        helper = "\n    private static string NormalizeComposeRepoPath(string? configuredPath)\n    {\n        var path = string.IsNullOrWhiteSpace(configuredPath)\n            ? \"/workspace\"\n            : configuredPath.Trim();\n\n        if (Directory.Exists(path))\n        {\n            return path;\n        }\n\n        if (Directory.Exists(\"/workspace\"))\n        {\n            return \"/workspace\";\n        }\n\n        return Directory.GetCurrentDirectory();\n    }\n"
        idx = s.rfind("}")
        if idx == -1:
            print("[argus fix] Could not append NormalizeComposeRepoPath helper.", file=sys.stderr)
        else:
            s = s[:idx] + helper + s[idx:]
            scaler.write_text(s)
            print("[argus fix] Added Docker Compose repo path normalization for worker autoscaler.")
    else:
        print("[argus fix] Could not patch DockerComposeWorkerScaler repoRoot assignment; please inspect manually.", file=sys.stderr)
else:
    print("[argus fix] DockerComposeWorkerScaler path normalization already present.")
PY

echo "[argus fix] Done."
echo "[argus fix] Recommended deploy:"
echo "  ARGUS_NO_UI=1 bash deploy/auto-all-in-one.sh --yes"
