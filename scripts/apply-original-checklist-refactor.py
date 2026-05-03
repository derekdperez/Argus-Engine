#!/usr/bin/env python3
"""
Apply the final repo-wide Argus Engine refactor safely and idempotently.

Why this script exists:
- A zip overlay cannot delete old directories or rename every existing file when unzipped.
- This script performs the destructive filesystem rename locally after the overlay is extracted.
- Database names and table names are deliberately preserved unless a later explicit DB migration is created.

Run from the project root:

    python scripts/apply-original-checklist-refactor.py --apply

Use --dry-run first to see planned changes.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import shutil
import sys

VERSION = "2.3.0"
FILE_VERSION = "2.3.0.0"

ROOT = Path(__file__).resolve().parents[1]

PROJECT_RENAMES = {
    "NightmareV2.Application": "ArgusEngine.Application",
    "NightmareV2.CommandCenter": "ArgusEngine.CommandCenter",
    "NightmareV2.Contracts": "ArgusEngine.Contracts",
    "NightmareV2.Domain": "ArgusEngine.Domain",
    "NightmareV2.Gatekeeper": "ArgusEngine.Gatekeeper",
    "NightmareV2.Infrastructure": "ArgusEngine.Infrastructure",
    "NightmareV2.Workers.Enum": "ArgusEngine.Workers.Enum",
    "NightmareV2.Workers.Spider": "ArgusEngine.Workers.Spider",
    "NightmareV2.Workers.PortScan": "ArgusEngine.Workers.PortScan",
    "NightmareV2.Workers.HighValue": "ArgusEngine.Workers.HighValue",
    "NightmareV2.Workers.TechnologyIdentification": "ArgusEngine.Workers.TechnologyIdentification",
}

TEST_RENAMES = {
    "NightmareV2.Application.Tests": "ArgusEngine.Application.Tests",
    "NightmareV2.CommandCenter.Tests": "ArgusEngine.CommandCenter.Tests",
    "NightmareV2.Infrastructure.Tests": "ArgusEngine.Infrastructure.Tests",
    "NightmareV2.TechnologyIdentification.Tests": "ArgusEngine.TechnologyIdentification.Tests",
    "NightmareV2.Workers.Enum.Tests": "ArgusEngine.Workers.Enum.Tests",
    "NightmareV2.Workers.Spider.Tests": "ArgusEngine.Workers.Spider.Tests",
}

TEXT_EXTENSIONS = {
    ".cs", ".csproj", ".slnx", ".razor", ".json", ".yaml", ".yml", ".props", ".targets",
    ".md", ".sh", ".ps1", ".html", ".css", ".js", ".txt", ".env", ".example", ".config"
}

TEXT_FILENAMES = {
    "Dockerfile", "Dockerfile.web", "Dockerfile.worker", "Dockerfile.worker-enum",
    "VERSION", ".env", ".env.example"
}

DIRECT_REPLACEMENTS = [
    ("NightmareV2.Contracts", "ArgusEngine.Contracts"),
    ("NightmareV2.Domain", "ArgusEngine.Domain"),
    ("NightmareV2.Application", "ArgusEngine.Application"),
    ("NightmareV2.Infrastructure", "ArgusEngine.Infrastructure"),
    ("NightmareV2.CommandCenter", "ArgusEngine.CommandCenter"),
    ("NightmareV2.Gatekeeper", "ArgusEngine.Gatekeeper"),
    ("NightmareV2.Workers", "ArgusEngine.Workers"),
    ("NightmareDbContext", "ArgusDbContext"),
    ("NightmareRuntimeOptions", "ArgusRuntimeOptions"),
    ("NightmareDbSeeder", "ArgusDbSeeder"),
    ("NightmareDbSchemaPatches", "ArgusDbSchemaPatches"),
    ("StartupDatabaseBootstrap", "ArgusStartupDatabaseBootstrap"),
    ("NightmareV2", "ArgusEngine"),
    ("N2 Nightmare Command Center", "Argus Engine Command Center"),
    ("Nightmare Command Center", "Argus Engine Command Center"),
    ("Nightmare v2", "Argus Engine"),
    ("Nightmare V2", "Argus Engine"),
    ("nightmare-v2", "argus-engine"),
    ("nightmare_v2", "argus_engine"),
]

# DB object names intentionally preserved.
PRESERVE_DB_NAMES = {
    "recon_targets", "stored_assets", "http_request_queue", "outbox_messages", "inbox_messages",
    "bus_journal", "high_value_findings", "technology_detections", "asset_relationships",
    "nightmare_v2", "nightmare_v2_files",
}

def is_text_file(path: Path) -> bool:
    if path.name in TEXT_FILENAMES:
        return True
    if path.suffix in TEXT_EXTENSIONS:
        return True
    return False

def iter_files(root: Path):
    excluded = {".git", "bin", "obj", "node_modules", ".vs"}
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        if any(part in excluded for part in path.parts):
            continue
        if is_text_file(path):
            yield path

def write(path: Path, text: str, apply: bool):
    if apply:
        path.write_text(text, encoding="utf-8", newline="")

def rename_path(src: Path, dst: Path, apply: bool):
    if not src.exists() or dst.exists():
        return
    print(f"rename {src.relative_to(ROOT)} -> {dst.relative_to(ROOT)}")
    if apply:
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(dst))

def rename_directories(apply: bool):
    for old, new in PROJECT_RENAMES.items():
        rename_path(ROOT / "src" / old, ROOT / "src" / new, apply)
    for old, new in TEST_RENAMES.items():
        rename_path(ROOT / "src" / "tests" / old, ROOT / "src" / "tests" / new, apply)

def rename_files(apply: bool):
    for path in sorted(ROOT.rglob("*"), reverse=True):
        if not path.is_file():
            continue
        if "bin" in path.parts or "obj" in path.parts or ".git" in path.parts:
            continue
        new_name = path.name
        for old, new in {**PROJECT_RENAMES, **TEST_RENAMES}.items():
            new_name = new_name.replace(old, new)
        for old, new in [
            ("NightmareDbContext", "ArgusDbContext"),
            ("NightmareRuntimeOptions", "ArgusRuntimeOptions"),
            ("NightmareDbSeeder", "ArgusDbSeeder"),
            ("NightmareDbSchemaPatches", "ArgusDbSchemaPatches"),
            ("NightmareV2", "ArgusEngine"),
        ]:
            new_name = new_name.replace(old, new)
        if new_name != path.name:
            rename_path(path, path.with_name(new_name), apply)

    rename_path(ROOT / "NightmareV2.slnx", ROOT / "ArgusEngine.slnx", apply)

def rewrite_text_files(apply: bool):
    for path in iter_files(ROOT):
        try:
            original = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        updated = original
        placeholders = {}
        for name in PRESERVE_DB_NAMES:
            token = f"__ARGUS_PRESERVE_{len(placeholders)}__"
            placeholders[token] = name
            updated = updated.replace(name, token)

        for old, new in DIRECT_REPLACEMENTS:
            updated = updated.replace(old, new)

        # Public Docker/compose/env keys: new Argus values are primary; legacy keys remain where
        # compatibility was explicitly added.
        updated = updated.replace("NIGHTMARE_BUILD_STAMP", "ARGUS_BUILD_STAMP")
        updated = updated.replace("NIGHTMARE_DIAGNOSTICS_API_KEY", "ARGUS_DIAGNOSTICS_API_KEY")
        updated = updated.replace("NIGHTMARE_DATA_MAINTENANCE_API_KEY", "ARGUS_DATA_MAINTENANCE_API_KEY")
        updated = updated.replace("Nightmare__", "Argus__")

        for token, name in placeholders.items():
            updated = updated.replace(token, name)

        if updated != original:
            print(f"rewrite {path.relative_to(ROOT)}")
            write(path, updated, apply)

def ensure_version_files(apply: bool):
    targets = ROOT / "Directory.Build.targets"
    version = ROOT / "VERSION"
    targets_text = f"""<Project>
  <PropertyGroup>
    <ArgusEngineDeploymentVersion>{VERSION}</ArgusEngineDeploymentVersion>
    <Version>$(ArgusEngineDeploymentVersion)</Version>
    <PackageVersion>$(ArgusEngineDeploymentVersion)</PackageVersion>
    <AssemblyVersion>{FILE_VERSION}</AssemblyVersion>
    <FileVersion>{FILE_VERSION}</FileVersion>
    <InformationalVersion>$(ArgusEngineDeploymentVersion)</InformationalVersion>
  </PropertyGroup>
</Project>
"""
    print("write Directory.Build.targets / VERSION")
    write(targets, targets_text, apply)
    write(version, VERSION + "\n", apply)

def ensure_migration_note(apply: bool):
    docs = ROOT / "docs"
    note = docs / "argus-engine-migration-note.md"
    if apply:
        docs.mkdir(exist_ok=True)
    text = """# Argus Engine Migration Note

Argus Engine was previously developed under the internal codename NightmareV2.

For transition safety, the application temporarily supports both:

- `Argus:*` and `Nightmare:*` configuration keys
- `ARGUS_*` and `NIGHTMARE_*` environment variables where compatibility was required
- existing database names and table names such as `nightmare_v2`, `stored_assets`, and `http_request_queue`

Do not rename production databases or tables without a separate explicit migration/backfill plan.
"""
    print("write docs/argus-engine-migration-note.md")
    write(note, text, apply)

def ensure_solution_hint(apply: bool):
    # The .slnx file is text and is already rewritten by replacement. This file documents the one command
    # contractors can run to regenerate solution metadata after destructive project directory renames.
    text = """# Solution Rename Follow-up

After running `scripts/apply-original-checklist-refactor.py --apply`, verify:

```bash
dotnet build ArgusEngine.slnx
dotnet test ArgusEngine.slnx
docker compose -f deploy/docker-compose.yml build
```

If the `.slnx` file still contains stale paths, regenerate project entries with your IDE or `dotnet sln`
after the directory rename has completed.
"""
    path = ROOT / "docs" / "solution-rename-follow-up.md"
    print("write docs/solution-rename-follow-up.md")
    write(path, text, apply)

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="Apply changes. Without this flag the script is a dry run.")
    args = parser.parse_args()
    apply = args.apply

    print(f"Argus Engine final refactor migration ({'apply' if apply else 'dry-run'})")
    rename_directories(apply)
    rename_files(apply)
    rewrite_text_files(apply)
    ensure_version_files(apply)
    ensure_migration_note(apply)
    ensure_solution_hint(apply)
    print("done")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
