#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f "ArgusEngine.slnx" ]]; then
  echo "Run from the repository root." >&2
  exit 1
fi

project_path='src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj'
if ! grep -q "$project_path" ArgusEngine.slnx; then
  python3 - <<'PY'
from pathlib import Path
path = Path("ArgusEngine.slnx")
text = path.read_text()
entry = '  <Project Path="src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj" />\n'
anchor = '  <Project Path="src/ArgusEngine.Workers.PortScan/ArgusEngine.Workers.PortScan.csproj" />\n'
if entry not in text:
    if anchor in text:
        text = text.replace(anchor, entry + anchor)
    else:
        text = text.replace('</Solution>', entry + '</Solution>')
path.write_text(text)
PY
fi

echo "ReconOrchestrator overlay applied."
echo "Optional build check: dotnet build src/ArgusEngine.Workers.Orchestration/ArgusEngine.Workers.Orchestration.csproj"
