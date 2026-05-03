#!/usr/bin/env python3
"""
Apply the repo-wide Argus Engine rename and final hardening updates.

A zip overlay can add and overwrite files, but it cannot delete or move the
existing NightmareV2 directories that are already in a checkout. This script
performs those filesystem renames locally, updates references, and preserves
database names/tables and legacy config compatibility.

Run from repository root:

    python scripts/apply-original-checklist-refactor.py --dry-run
    python scripts/apply-original-checklist-refactor.py --apply
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import shutil
import sys

VERSION = "2.4.0"
FILE_VERSION = "2.4.0.0"
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
    "NightmareV2.CommandCenter.Tests": "ArgusEngine.CommandCenter.Tests",
    "NightmareV2.Infrastructure.Tests": "ArgusEngine.Infrastructure.Tests",
    "NightmareV2.Application.Tests": "ArgusEngine.Application.Tests",
    "NightmareV2.Workers.Enum.Tests": "ArgusEngine.Workers.Enum.Tests",
    "NightmareV2.Workers.Spider.Tests": "ArgusEngine.Workers.Spider.Tests",
    "NightmareV2.TechnologyIdentification.Tests": "ArgusEngine.TechnologyIdentification.Tests",
}

TYPE_RENAMES = {
    "NightmareDbContext": "ArgusDbContext",
    "NightmareRuntimeOptions": "ArgusRuntimeOptions",
    "NightmareDbSeeder": "ArgusDbSeeder",
    "NightmareDbSchemaPatches": "ArgusDbSchemaPatches",
}

TEXT_EXTENSIONS = {
    ".cs", ".csproj", ".slnx", ".razor", ".json", ".yaml", ".yml", ".props", ".targets",
    ".md", ".sh", ".ps1", ".html", ".css", ".js", ".txt", ".env", ".config", ".xml"
}
TEXT_NAMES = {"Dockerfile", "Dockerfile.web", "Dockerfile.worker", "Dockerfile.worker-enum", "VERSION"}

# These identifiers must not be renamed by the product rename. They are database
# compatibility boundaries and must remain until an explicit DB migration exists.
PRESERVE_TOKENS = [
    "nightmare_v2",
    "nightmare_v2_files",
    "recon_targets",
    "stored_assets",
    "http_request_queue",
    "outbox_messages",
    "inbox_messages",
    "bus_journal",
    "high_value_findings",
    "technology_detections",
    "asset_relationships",
]

def should_skip(path: Path) -> bool:
    return any(part in {".git", "bin", "obj", "node_modules", ".vs"} for part in path.parts)

def is_text(path: Path) -> bool:
    return path.name in TEXT_NAMES or path.suffix in TEXT_EXTENSIONS

def move(src: Path, dst: Path, apply: bool) -> None:
    if not src.exists():
        return
    if dst.exists():
        print(f"skip existing {dst.relative_to(ROOT)}")
        return
    print(f"rename {src.relative_to(ROOT)} -> {dst.relative_to(ROOT)}")
    if apply:
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(dst))

def rename_directories(apply: bool) -> None:
    for old, new in PROJECT_RENAMES.items():
        move(ROOT / "src" / old, ROOT / "src" / new, apply)
    for old, new in TEST_RENAMES.items():
        move(ROOT / "src" / "tests" / old, ROOT / "src" / "tests" / new, apply)

def rename_files(apply: bool) -> None:
    rename_map = {**PROJECT_RENAMES, **TEST_RENAMES, **TYPE_RENAMES, "NightmareV2": "ArgusEngine"}

    for path in sorted(ROOT.rglob("*"), key=lambda p: len(p.parts), reverse=True):
        if should_skip(path) or not path.is_file():
            continue

        new_name = path.name
        for old, new in rename_map.items():
            new_name = new_name.replace(old, new)

        if new_name != path.name:
            move(path, path.with_name(new_name), apply)

    move(ROOT / "NightmareV2.slnx", ROOT / "ArgusEngine.slnx", apply)

def rewrite_text_files(apply: bool) -> None:
    replacements = [
        ("NightmareV2.Contracts", "ArgusEngine.Contracts"),
        ("NightmareV2.Domain", "ArgusEngine.Domain"),
        ("NightmareV2.Application", "ArgusEngine.Application"),
        ("NightmareV2.Infrastructure", "ArgusEngine.Infrastructure"),
        ("NightmareV2.CommandCenter", "ArgusEngine.CommandCenter"),
        ("NightmareV2.Gatekeeper", "ArgusEngine.Gatekeeper"),
        ("NightmareV2.Workers", "ArgusEngine.Workers"),
        *TYPE_RENAMES.items(),
        ("N2 Nightmare Command Center", "Argus Engine Command Center"),
        ("Nightmare Command Center", "Argus Engine Command Center"),
        ("Nightmare v2", "Argus Engine"),
        ("Nightmare V2", "Argus Engine"),
        ("nightmare-v2", "argus-engine"),
        ("nightmare-worker", "argus-worker"),
    ]

    for path in ROOT.rglob("*"):
        if should_skip(path) or not path.is_file() or not is_text(path):
            continue

        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        placeholders: dict[str, str] = {}
        updated = text
        for i, token in enumerate(PRESERVE_TOKENS):
            key = f"__ARGUS_DB_PRESERVE_{i}__"
            placeholders[key] = token
            updated = updated.replace(token, key)

        for old, new in replacements:
            updated = updated.replace(old, new)

        # Build stamp changes are additive. Keep NIGHTMARE_BUILD_STAMP fallback
        # in existing files while making ARGUS_BUILD_STAMP primary.
        updated = updated.replace("Environment.GetEnvironmentVariable(\"NIGHTMARE_BUILD_STAMP\")",
                                  "Environment.GetEnvironmentVariable(\"ARGUS_BUILD_STAMP\") ?? Environment.GetEnvironmentVariable(\"NIGHTMARE_BUILD_STAMP\")")

        for key, token in placeholders.items():
            updated = updated.replace(key, token)

        if updated != text:
            print(f"rewrite {path.relative_to(ROOT)}")
            if apply:
                path.write_text(updated, encoding="utf-8", newline="")

def write_version_files(apply: bool) -> None:
    targets = ROOT / "Directory.Build.targets"
    text = f"""<Project>
  <PropertyGroup>
    <ArgusEngineDeploymentVersion>{VERSION}</ArgusEngineDeploymentVersion>
    <Version>$(ArgusEngineDeploymentVersion)</Version>
    <PackageVersion>$(ArgusEngineDeploymentVersion)</PackageVersion>
    <AssemblyVersion>{FILE_VERSION}</AssemblyVersion>
    <FileVersion>{FILE_VERSION}</FileVersion>
    <InformationalVersion>$(ArgusEngineDeploymentVersion)</InformationalVersion>
  </PropertyGroup>

  <Target Name="ValidateArgusEngineDeploymentVersion" BeforeTargets="GenerateAssemblyInfo">
    <Error Condition="'$(ArgusEngineDeploymentVersion)' == ''" Text="ArgusEngineDeploymentVersion must be set before deploying." />
  </Target>
</Project>
"""
    print("write Directory.Build.targets and VERSION")
    if apply:
        targets.write_text(text, encoding="utf-8", newline="")
        (ROOT / "VERSION").write_text(VERSION + "\n", encoding="utf-8")

def write_solution_file(apply: bool) -> None:
    projects = [
        "ArgusEngine.Application",
        "ArgusEngine.CommandCenter",
        "ArgusEngine.Contracts",
        "ArgusEngine.Domain",
        "ArgusEngine.Gatekeeper",
        "ArgusEngine.Infrastructure",
        "ArgusEngine.Workers.Enum",
        "ArgusEngine.Workers.Spider",
        "ArgusEngine.Workers.PortScan",
        "ArgusEngine.Workers.HighValue",
        "ArgusEngine.Workers.TechnologyIdentification",
    ]
    tests_root = ROOT / "src" / "tests"
    test_projects = sorted(p.parent.name for p in tests_root.glob("ArgusEngine*.Tests/*.csproj")) if tests_root.exists() else []

    lines = ["<Solution>", "  <Folder Name=\"/src/\">"]
    for name in projects:
        lines.append(f"    <Project Path=\"src/{name}/{name}.csproj\" Type=\"Classic C#\" />")
    lines.append("  </Folder>")
    if test_projects:
        lines.append("  <Folder Name=\"/tests/\">")
        for name in test_projects:
            lines.append(f"    <Project Path=\"src/tests/{name}/{name}.csproj\" Type=\"Classic C#\" />")
        lines.append("  </Folder>")
    lines.append("</Solution>")
    print("write ArgusEngine.slnx")
    if apply:
        (ROOT / "ArgusEngine.slnx").write_text("\n".join(lines) + "\n", encoding="utf-8")

def write_report(apply: bool) -> None:
    report = {
        "version": VERSION,
        "renamedProjects": PROJECT_RENAMES,
        "renamedTests": TEST_RENAMES,
        "renamedTypes": TYPE_RENAMES,
        "preservedDatabaseTokens": PRESERVE_TOKENS,
        "postApplyCommands": [
            "dotnet build ArgusEngine.slnx",
            "dotnet test ArgusEngine.slnx",
            "docker compose -f deploy/docker-compose.yml build",
            "docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.observability.yml up -d --build",
        ],
    }
    print("write docs/refactor-apply-report.json")
    if apply:
        path = ROOT / "docs" / "refactor-apply-report.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    apply = args.apply and not args.dry_run
    print(f"Argus Engine refactor migration ({'apply' if apply else 'dry-run'})")

    rename_directories(apply)
    rename_files(apply)
    rewrite_text_files(apply)
    write_version_files(apply)
    write_solution_file(apply)
    write_report(apply)

    print("done")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
