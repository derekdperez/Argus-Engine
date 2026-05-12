#!/usr/bin/env python3
from pathlib import Path
import sys

path = Path("src/ArgusEngine.CommandCenter.Web/Components/Pages/OpsRadzen.razor")

old = """}  @if (_initialLoadStarted && _targetsLoading && TotalTargets == 0 && ConfirmedAssets == 0) {  Loading...
— } else {  Targets @FormatNumber(TotalTargets) Assets @FormatNumber(ConfirmedAssets) Subdomains @FormatNumber(ConfirmedSubdomains) Unique Technologies @FormatNumber(UniqueTechnologies) HTTP Queue @(_httpLoading && HttpQueueQueuedCount == 0 ? "—" : $"{FormatNumber(HttpQueueQueuedCount)} queued / {FormatNumber(HttpRequestsSentPerMinute)} sent/min") Asset Storage @FormatStorageSize(TotalTrackedStorageBytes) @StorageBreakdownLabel }"""

new = """}  @if (_initialLoadStarted && _targetsLoading && TotalTargets == 0 && ConfirmedAssets == 0) {  Targets — Assets — Subdomains — Unique Technologies — HTTP Queue — Asset Storage —
} else {  Targets @FormatNumber(TotalTargets) Assets @FormatNumber(ConfirmedAssets) Subdomains @FormatNumber(ConfirmedSubdomains) Unique Technologies @FormatNumber(UniqueTechnologies) HTTP Queue @(_httpLoading && HttpQueueQueuedCount == 0 ? "—" : $"{FormatNumber(HttpQueueQueuedCount)} queued / {FormatNumber(HttpRequestsSentPerMinute)} sent/min") Asset Storage @FormatStorageSize(TotalTrackedStorageBytes) @StorageBreakdownLabel }"""

if not path.exists():
    print(f"ERROR: {path} was not found. Run this script from the repository root.", file=sys.stderr)
    sys.exit(1)

text = path.read_text(encoding="utf-8")

if new in text:
    print("Ops summary-card dash placeholders are already applied.")
    sys.exit(0)

if old not in text:
    print("ERROR: Expected Operations summary-card loading block was not found.", file=sys.stderr)
    print("The file may have changed. Inspect the first summary-card branch in:", path, file=sys.stderr)
    sys.exit(2)

backup = path.with_suffix(path.suffix + ".bak")
if not backup.exists():
    backup.write_text(text, encoding="utf-8")

path.write_text(text.replace(old, new, 1), encoding="utf-8")
print(f"Patched {path}")
print(f"Backup saved to {backup}")
