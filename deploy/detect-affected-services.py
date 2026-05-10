#!/usr/bin/env python3
"""Detect deployable Argus services affected by a Git diff.

The detector intentionally avoids using the solution file as a global invalidator.
Instead, it maps changes to each service's transitive ProjectReference closure,
plus service-specific resources and Docker recipe files.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


GLOBAL_ALL_SERVICE_FILES = {
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.config",
    "global.json",
    ".dockerignore",
    "deploy/service-catalog.tsv",
    "deploy/lib-argus-service-catalog.sh",
    "deploy/detect-affected-services.py",
    "deploy/aws/build-push-ecr.sh",
    "deploy/aws/create-ecr-repos.sh",
    "deploy/aws/deploy-ecs-services.sh",
    ".github/workflows/release-main.yml",
}

# Conservative runtime deploy config: this may alter environment, images, or
# replica behavior. It is still scoped to ECR/ECS-deployable services, not every
# local-only compose helper.
RUNTIME_ALL_SERVICE_FILES = {
    "deploy/docker-compose.yml",
    "deploy/.env",
    ".env",
}

DOCKERFILE_RESOURCE_HINTS = {
    "deploy/Dockerfile.base-runtime": "all",
    "deploy/Dockerfile.base-recon": {"worker-enum"},
    "deploy/wordlists": {"worker-enum"},
    "deploy/artifacts/recon-tools": {"worker-enum"},
}


def run_git(args: list[str], cwd: Path, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *args],
        cwd=cwd,
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def normalize_path(path: str | Path) -> str:
    return str(path).replace("\\", "/").strip("/")


def path_is_under(path: str, root: str) -> bool:
    root = root.rstrip("/")
    return path == root or path.startswith(root + "/")


def parse_catalog(path: Path) -> list[dict[str, object]]:
    services: list[dict[str, object]] = []

    with path.open(encoding="utf-8") as handle:
        for raw in handle:
            line = raw.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue

            parts = line.split("\t")
            if len(parts) < 7:
                raise ValueError(f"Invalid service catalog row: {line!r}")

            service, project_dir, app_dll, dockerfile, ecr_enabled, kind, extras = parts[:7]
            services.append(
                {
                    "service": service,
                    "project_dir": project_dir,
                    "project_path": f"src/{project_dir}",
                    "csproj": f"src/{project_dir}/{project_dir}.csproj",
                    "app_dll": app_dll,
                    "dockerfile": dockerfile,
                    "ecr_enabled": ecr_enabled == "1",
                    "kind": kind,
                    "extra_source_dirs": [item for item in extras.split(",") if item],
                }
            )

    return services


def find_project_references(csproj: Path) -> list[Path]:
    if not csproj.exists():
        return []

    try:
        tree = ET.parse(csproj)
    except ET.ParseError:
        return []

    refs: list[Path] = []
    for item in tree.iter():
        if item.tag.split("}")[-1] != "ProjectReference":
            continue

        include = item.attrib.get("Include")
        if include:
            refs.append((csproj.parent / include).resolve())

    return refs


def project_reference_closure(repo_root: Path, service: dict[str, object]) -> set[str]:
    start = (repo_root / str(service["csproj"])).resolve()
    visited: set[Path] = set()
    dirs: set[str] = set()

    def visit(csproj: Path) -> None:
        if csproj in visited:
            return

        visited.add(csproj)
        if csproj.exists():
            try:
                dirs.add(normalize_path(csproj.parent.relative_to(repo_root)))
            except ValueError:
                pass

        for ref in find_project_references(csproj):
            visit(ref)

    visit(start)
    dirs.add(str(service["project_path"]))
    return dirs


def resolve_base_ref(repo_root: Path, explicit_base: str | None, head: str) -> str | None:
    if explicit_base:
        return explicit_base

    before = os.environ.get("GITHUB_EVENT_BEFORE", "").strip()
    if before and set(before) != {"0"}:
        return before

    event_name = os.environ.get("GITHUB_EVENT_NAME", "").strip()
    if event_name == "workflow_dispatch":
        return None

    # Shallow or first-commit clones may not have HEAD^.
    probe = run_git(["rev-parse", "HEAD^"], cwd=repo_root, check=False)
    if probe.returncode == 0:
        return probe.stdout.strip()

    return None


def changed_files(repo_root: Path, base: str | None, head: str) -> tuple[list[str] | None, str]:
    if not base:
        return None, "no-base-ref"

    merge_base = run_git(["merge-base", base, head], cwd=repo_root, check=False)
    diff_base = merge_base.stdout.strip() if merge_base.returncode == 0 else base

    diff = run_git(["diff", "--name-only", f"{diff_base}..{head}"], cwd=repo_root, check=False)
    if diff.returncode != 0:
        return None, "diff-failed"

    files = [normalize_path(line) for line in diff.stdout.splitlines() if line.strip()]
    return files, f"{diff_base}..{head}"


def detect(repo_root: Path, catalog_path: Path, base: str | None, head: str) -> tuple[list[str], dict[str, object]]:
    services = [service for service in parse_catalog(catalog_path) if service["ecr_enabled"]]
    all_services = [str(service["service"]) for service in services]
    files, diff_range = changed_files(repo_root, base, head)

    metadata: dict[str, object] = {
        "diff_range": diff_range,
        "changed_files": files,
        "reason": None,
    }

    if files is None:
        metadata["reason"] = "no reliable diff; deploying all services"
        return all_services, metadata

    if not files:
        metadata["reason"] = "no changed files"
        return [], metadata

    affected: set[str] = set()
    dockerfile_to_services: dict[str, set[str]] = {}
    closures: dict[str, set[str]] = {}

    for service in services:
        name = str(service["service"])
        dockerfile_to_services.setdefault(str(service["dockerfile"]), set()).add(name)
        closure = project_reference_closure(repo_root, service)
        closure.update(str(path) for path in service["extra_source_dirs"])
        closures[name] = closure

    for changed in files:
        if changed in GLOBAL_ALL_SERVICE_FILES or changed in RUNTIME_ALL_SERVICE_FILES:
            metadata["reason"] = f"{changed} affects deployment globally"
            return all_services, metadata

        for hint_path, hint_services in DOCKERFILE_RESOURCE_HINTS.items():
            if path_is_under(changed, hint_path):
                if hint_services == "all":
                    metadata["reason"] = f"{changed} affects the shared runtime image"
                    return all_services, metadata
                affected.update(hint_services)

        if changed in dockerfile_to_services:
            affected.update(dockerfile_to_services[changed])

        for service_name, source_dirs in closures.items():
            if any(path_is_under(changed, source_dir) for source_dir in source_dirs):
                affected.add(service_name)

    ordered = [service for service in all_services if service in affected]
    metadata["reason"] = "service-scoped changes"
    return ordered, metadata


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", default=".", help="Repository root. Defaults to current directory.")
    parser.add_argument("--catalog", default="deploy/service-catalog.tsv", help="Path to service catalog TSV.")
    parser.add_argument("--base", default=None, help="Base Git ref/SHA. Defaults to GITHUB_EVENT_BEFORE or HEAD^.")
    parser.add_argument("--head", default="HEAD", help="Head Git ref/SHA. Defaults to HEAD.")
    parser.add_argument(
        "--format",
        choices=("space", "lines", "json", "github-output"),
        default="space",
        help="Output format.",
    )
    args = parser.parse_args()

    repo_root = Path(args.repo_root).resolve()
    catalog = (repo_root / args.catalog).resolve()
    base = resolve_base_ref(repo_root, args.base, args.head)

    services, metadata = detect(repo_root, catalog, base, args.head)

    if args.format == "lines":
        print("\n".join(services))
    elif args.format == "json":
        print(json.dumps({"services": services, **metadata}, indent=2, sort_keys=True))
    elif args.format == "github-output":
        output_path = os.environ.get("GITHUB_OUTPUT")
        lines = [
            f"services={' '.join(services)}",
            f"has_changes={'true' if services else 'false'}",
            f"diff_range={metadata.get('diff_range', '')}",
            f"reason={metadata.get('reason', '')}",
        ]

        if output_path:
            with open(output_path, "a", encoding="utf-8") as handle:
                for line in lines:
                    handle.write(line + "\n")
        else:
            print("\n".join(lines))
    else:
        print(" ".join(services))

    return 0


if __name__ == "__main__":
    sys.exit(main())
