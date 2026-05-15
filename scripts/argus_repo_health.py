#!/usr/bin/env python3
"""
argus_repo_health.py

Deterministic, read-only repository health snapshot for Argus Engine AI agents.

Design goals:
  - Python standard library only.
  - No repo mutations by default.
  - Stable JSON schema and sorted output for AI consumption.
  - Useful even on fresh/local/cloud hosts before dependencies are installed.
  - Conservative risk flags for accidental deletions, regressions, drift, and deploy readiness.

Examples:
  python3 scripts/argus_repo_health.py
  python3 scripts/argus_repo_health.py --format markdown
  python3 scripts/argus_repo_health.py --format compact --expect-running
  python3 scripts/argus_repo_health.py --repo-root /path/to/argus-engine --output .ai/repo-health.json

Exit codes:
  0 = overall status ok
  1 = warnings present
  2 = failures present
  3 = script/repo discovery error
"""

from __future__ import annotations

import argparse
import ast
import datetime as _dt
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Mapping, Optional, Sequence


SCHEMA_VERSION = "1.0"

DEFAULT_BASELINE_REFS = ["@{u}", "origin/main", "main"]

CRITICAL_PATHS = [
    "AGENTS.md",
    "README.md",
    "ArgusEngine.slnx",
    "VERSION",
    "global.json",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "deploy.py",
    "test.sh",
    "deployment",
    "deployment/docker-compose.yml",
    "deployment/service-catalog.tsv",
    "deployment/aws",
    "deployment/azure",
    "deployment/gcp",
    ".github/workflows",
    "src/ArgusEngine.Contracts",
    "src/ArgusEngine.CommandCenter.Contracts",
    "src/ArgusEngine.Domain",
    "src/ArgusEngine.Infrastructure",
    "src/ArgusEngine.Application",
    "src/ArgusEngine.CommandCenter.Gateway",
    "src/ArgusEngine.CommandCenter.Web",
    "src/ArgusEngine.Gatekeeper",
    "src/ArgusEngine.Workers.Spider",
    "src/ArgusEngine.Workers.Enumeration",
    "src/ArgusEngine.Workers.HttpRequester",
    "src/ArgusEngine.Workers.PortScan",
    "src/ArgusEngine.Workers.HighValue",
    "src/ArgusEngine.Workers.TechnologyIdentification",
]

IMPORTANT_FILES = [
    "AGENTS.md",
    "README.md",
    "ArgusEngine.slnx",
    "VERSION",
    "global.json",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.config",
    "deploy.py",
    "test.sh",
    "debug.sh",
    "deployment/docker-compose.yml",
    "deployment/docker-compose.ci.yml",
    "deployment/service-catalog.tsv",
    "deployment/Dockerfile.base-runtime",
    "deployment/Dockerfile.base-recon",
    "deployment/Dockerfile.commandcenter-host",
    "deployment/Dockerfile.worker",
    "deployment/Dockerfile.worker-enum",
    ".github/workflows/ci.yml",
    ".github/workflows/release-main.yml",
]

DEFAULT_HEALTH_ENDPOINTS = {
    "command-center-gateway": "http://127.0.0.1:8081/health/ready",
    "command-center-web": "http://127.0.0.1:8082/health/ready",
    "command-center-operations-api": "http://127.0.0.1:8083/health/ready",
    "command-center-discovery-api": "http://127.0.0.1:8084/health/ready",
    "command-center-worker-control-api": "http://127.0.0.1:8085/health/ready",
    "command-center-maintenance-api": "http://127.0.0.1:8086/health/ready",
    "command-center-updates-api": "http://127.0.0.1:8087/health/ready",
    "command-center-realtime": "http://127.0.0.1:8088/health/ready",
    "command-center-cloud-deploy-api": "http://127.0.0.1:8089/healthz",
}

SECRET_FILE_PATTERNS = [
    re.compile(r"(^|/)\.env(\..*)?$", re.IGNORECASE),
    re.compile(r"\.(pem|pfx|p12|key|kubeconfig)$", re.IGNORECASE),
    re.compile(r"(^|/)(id_rsa|id_dsa|id_ecdsa|id_ed25519)$", re.IGNORECASE),
]

SENSITIVE_ENV_KEYS = [
    "AWS_ACCESS_KEY_ID",
    "AWS_PROFILE",
    "AWS_REGION",
    "GOOGLE_APPLICATION_CREDENTIALS",
    "GOOGLE_CLOUD_PROJECT",
    "AZURE_CLIENT_ID",
    "AZURE_TENANT_ID",
    "AZURE_SUBSCRIPTION_ID",
    "ARGUS_DIAGNOSTICS_API_KEY",
    "NIGHTMARE_DIAGNOSTICS_API_KEY",
]

VERSION_FILES = [
    "VERSION",
    "Directory.Build.targets",
    "Directory.Build.props",
    "global.json",
    "Directory.Packages.props",
]


@dataclass(frozen=True)
class CmdResult:
    args: list[str]
    cwd: str
    returncode: int
    stdout: str
    stderr: str
    timed_out: bool
    duration_ms: int

    def short(self, max_chars: int = 4000) -> dict[str, Any]:
        stdout = self.stdout.strip()
        stderr = self.stderr.strip()
        if len(stdout) > max_chars:
            stdout = stdout[:max_chars] + "\n...[truncated]"
        if len(stderr) > max_chars:
            stderr = stderr[:max_chars] + "\n...[truncated]"
        return {
            "args": self.args,
            "returncode": self.returncode,
            "stdout": stdout,
            "stderr": stderr,
            "timed_out": self.timed_out,
            "duration_ms": self.duration_ms,
        }


def utc_now_iso() -> str:
    return _dt.datetime.now(_dt.timezone.utc).replace(microsecond=0).isoformat()


def normalize_rel(path: str | Path) -> str:
    return str(path).replace("\\", "/").strip("/")


def is_under(path: str, parent: str) -> bool:
    path = normalize_rel(path)
    parent = normalize_rel(parent)
    return path == parent or path.startswith(parent + "/")


def run_cmd(
    args: Sequence[str],
    *,
    cwd: Path,
    timeout: float = 10.0,
    env: Optional[Mapping[str, str]] = None,
) -> CmdResult:
    started = time.monotonic()
    merged_env = os.environ.copy()
    if env:
        merged_env.update(env)

    try:
        completed = subprocess.run(
            list(args),
            cwd=str(cwd),
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
            env=merged_env,
        )
        duration_ms = int((time.monotonic() - started) * 1000)
        return CmdResult(
            args=list(args),
            cwd=str(cwd),
            returncode=completed.returncode,
            stdout=completed.stdout,
            stderr=completed.stderr,
            timed_out=False,
            duration_ms=duration_ms,
        )
    except subprocess.TimeoutExpired as ex:
        duration_ms = int((time.monotonic() - started) * 1000)
        stdout = ex.stdout if isinstance(ex.stdout, str) else (ex.stdout or b"").decode(errors="replace")
        stderr = ex.stderr if isinstance(ex.stderr, str) else (ex.stderr or b"").decode(errors="replace")
        return CmdResult(
            args=list(args),
            cwd=str(cwd),
            returncode=124,
            stdout=stdout,
            stderr=stderr,
            timed_out=True,
            duration_ms=duration_ms,
        )
    except FileNotFoundError as ex:
        duration_ms = int((time.monotonic() - started) * 1000)
        return CmdResult(
            args=list(args),
            cwd=str(cwd),
            returncode=127,
            stdout="",
            stderr=str(ex),
            timed_out=False,
            duration_ms=duration_ms,
        )
    except OSError as ex:
        duration_ms = int((time.monotonic() - started) * 1000)
        return CmdResult(
            args=list(args),
            cwd=str(cwd),
            returncode=126,
            stdout="",
            stderr=str(ex),
            timed_out=False,
            duration_ms=duration_ms,
        )


def read_text(path: Path, max_bytes: int = 2_000_000) -> str:
    try:
        data = path.read_bytes()
    except OSError:
        return ""
    if len(data) > max_bytes:
        data = data[:max_bytes]
    return data.decode("utf-8", errors="replace")


def read_json(path: Path) -> dict[str, Any]:
    text = read_text(path)
    if not text.strip():
        return {}
    try:
        loaded = json.loads(text)
        return loaded if isinstance(loaded, dict) else {}
    except json.JSONDecodeError:
        return {}


def sha256_file(path: Path, max_bytes: int = 10_000_000) -> Optional[str]:
    try:
        size = path.stat().st_size
        if size > max_bytes:
            return None
        digest = hashlib.sha256()
        with path.open("rb") as f:
            for chunk in iter(lambda: f.read(1024 * 128), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except OSError:
        return None


def count_files(path: Path, *, max_count: int = 5000) -> dict[str, Any]:
    count = 0
    dirs = 0
    truncated = False
    if not path.exists():
        return {"exists": False, "file_count": 0, "dir_count": 0, "truncated": False}
    if path.is_file():
        return {"exists": True, "file_count": 1, "dir_count": 0, "truncated": False}
    try:
        for child in path.rglob("*"):
            if ".git" in child.parts:
                continue
            if child.is_dir():
                dirs += 1
            else:
                count += 1
            if count + dirs >= max_count:
                truncated = True
                break
    except OSError:
        truncated = True
    return {"exists": True, "file_count": count, "dir_count": dirs, "truncated": truncated}


def find_repo_root(start: Optional[Path]) -> Optional[Path]:
    if start:
        candidates = [start.resolve(), *start.resolve().parents]
    else:
        candidates = [Path.cwd().resolve(), *Path.cwd().resolve().parents]

    for candidate in candidates:
        if (candidate / ".git").exists() and (
            (candidate / "ArgusEngine.slnx").exists()
            or (candidate / "deployment" / "docker-compose.yml").exists()
            or (candidate / "deploy.py").exists()
        ):
            return candidate

    git = shutil.which("git")
    if git:
        result = run_cmd(["git", "rev-parse", "--show-toplevel"], cwd=Path.cwd(), timeout=5)
        if result.returncode == 0:
            root = Path(result.stdout.strip())
            if root.exists():
                return root.resolve()

    return None


def parse_status_porcelain(text: str) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []
    for raw in text.splitlines():
        if not raw or raw.startswith("##"):
            continue
        if len(raw) < 4:
            continue
        xy = raw[:2]
        path_part = raw[3:].strip()
        old_path = None
        path = path_part
        if " -> " in path_part:
            old_path, path = path_part.split(" -> ", 1)
        entries.append(
            {
                "xy": xy,
                "index": xy[0],
                "worktree": xy[1],
                "path": normalize_rel(path.strip('"')),
                "old_path": normalize_rel(old_path.strip('"')) if old_path else None,
            }
        )
    return sorted(entries, key=lambda x: (x["path"], x["xy"]))


def parse_name_status(text: str) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []
    for raw in text.splitlines():
        if not raw.strip():
            continue
        parts = raw.split("\t")
        status = parts[0]
        if status.startswith("R") or status.startswith("C"):
            old_path = normalize_rel(parts[1]) if len(parts) > 1 else None
            path = normalize_rel(parts[2]) if len(parts) > 2 else old_path
        else:
            old_path = None
            path = normalize_rel(parts[1]) if len(parts) > 1 else ""
        entries.append({"status": status, "path": path, "old_path": old_path})
    return sorted(entries, key=lambda x: (x.get("path") or "", x.get("status") or ""))


def command_exists(name: str) -> bool:
    return shutil.which(name) is not None


def parse_semver(value: str) -> Optional[tuple[int, ...]]:
    match = re.search(r"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?", value or "")
    if not match:
        return None
    return tuple(int(p) for p in match.groups(default="0"))


def compare_versions(a: str, b: str) -> Optional[int]:
    va = parse_semver(a)
    vb = parse_semver(b)
    if va is None or vb is None:
        return None
    length = max(len(va), len(vb))
    va = va + (0,) * (length - len(va))
    vb = vb + (0,) * (length - len(vb))
    if va < vb:
        return -1
    if va > vb:
        return 1
    return 0


def regex_first(text: str, pattern: str) -> Optional[str]:
    match = re.search(pattern, text, re.IGNORECASE | re.MULTILINE | re.DOTALL)
    return match.group(1).strip() if match else None


def parse_xml_property(text: str, tag: str) -> Optional[str]:
    return regex_first(text, rf"<{re.escape(tag)}>\s*([^<]+?)\s*</{re.escape(tag)}>")


def extract_repo_version_values(root: Path, *, git_ref: Optional[str] = None) -> dict[str, Any]:
    values: dict[str, Any] = {}

    def get_file(rel: str) -> str:
        if not git_ref:
            return read_text(root / rel)
        result = run_cmd(["git", "show", f"{git_ref}:{rel}"], cwd=root, timeout=5)
        return result.stdout if result.returncode == 0 else ""

    version_text = get_file("VERSION").strip()
    if version_text:
        values["VERSION"] = version_text.splitlines()[0].strip()

    targets_text = get_file("Directory.Build.targets")
    if targets_text:
        values["Directory.Build.targets:Version"] = parse_xml_property(targets_text, "Version")
        values["Directory.Build.targets:AssemblyVersion"] = parse_xml_property(targets_text, "AssemblyVersion")
        values["Directory.Build.targets:FileVersion"] = parse_xml_property(targets_text, "FileVersion")

    props_text = get_file("Directory.Build.props")
    if props_text:
        values["Directory.Build.props:TargetFramework"] = parse_xml_property(props_text, "TargetFramework")
        values["Directory.Build.props:TargetFrameworks"] = parse_xml_property(props_text, "TargetFrameworks")

    global_json_text = get_file("global.json")
    if global_json_text:
        try:
            loaded = json.loads(global_json_text)
            values["global.json:sdk.version"] = loaded.get("sdk", {}).get("version")
            values["global.json:sdk.rollForward"] = loaded.get("sdk", {}).get("rollForward")
        except json.JSONDecodeError:
            values["global.json:parse_error"] = True

    return {k: v for k, v in sorted(values.items()) if v not in (None, "")}


def choose_baseline_ref(root: Path, requested: Optional[str]) -> Optional[str]:
    refs = [requested] if requested else []
    refs += DEFAULT_BASELINE_REFS
    seen: set[str] = set()
    for ref in refs:
        if not ref or ref in seen:
            continue
        seen.add(ref)
        result = run_cmd(["git", "rev-parse", "--verify", ref], cwd=root, timeout=5)
        if result.returncode == 0:
            return ref
    return None


def git_snapshot(root: Path, baseline_ref: Optional[str]) -> dict[str, Any]:
    git_info: dict[str, Any] = {
        "available": command_exists("git"),
        "baseline_ref": None,
        "branch": None,
        "head": None,
        "head_short": None,
        "upstream": None,
        "ahead": None,
        "behind": None,
        "is_clean": None,
        "status_entries": [],
        "changed_vs_baseline": [],
        "deleted_vs_baseline": [],
        "recent_commits": [],
        "errors": [],
    }

    if not command_exists("git"):
        git_info["errors"].append("git not found on PATH")
        return git_info

    inside = run_cmd(["git", "rev-parse", "--is-inside-work-tree"], cwd=root, timeout=5)
    if inside.returncode != 0:
        git_info["errors"].append(inside.stderr.strip() or "not a git worktree")
        return git_info

    branch = run_cmd(["git", "branch", "--show-current"], cwd=root, timeout=5)
    head = run_cmd(["git", "rev-parse", "HEAD"], cwd=root, timeout=5)
    upstream = run_cmd(["git", "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], cwd=root, timeout=5)
    status = run_cmd(["git", "status", "--porcelain=v1", "--branch"], cwd=root, timeout=10)

    git_info["branch"] = branch.stdout.strip() if branch.returncode == 0 else None
    if head.returncode == 0:
        git_info["head"] = head.stdout.strip()
        git_info["head_short"] = head.stdout.strip()[:12]
    git_info["upstream"] = upstream.stdout.strip() if upstream.returncode == 0 else None
    git_info["status_entries"] = parse_status_porcelain(status.stdout) if status.returncode == 0 else []
    git_info["is_clean"] = len(git_info["status_entries"]) == 0

    if upstream.returncode == 0:
        counts = run_cmd(["git", "rev-list", "--left-right", "--count", "HEAD...@{u}"], cwd=root, timeout=10)
        if counts.returncode == 0:
            parts = counts.stdout.strip().split()
            if len(parts) == 2:
                git_info["ahead"] = int(parts[0])
                git_info["behind"] = int(parts[1])

    baseline = choose_baseline_ref(root, baseline_ref)
    git_info["baseline_ref"] = baseline

    if baseline:
        diff = run_cmd(["git", "diff", "--name-status", f"{baseline}...HEAD"], cwd=root, timeout=15)
        deleted = run_cmd(["git", "diff", "--name-only", "--diff-filter=D", f"{baseline}...HEAD"], cwd=root, timeout=15)
        shortstat = run_cmd(["git", "diff", "--shortstat", f"{baseline}...HEAD"], cwd=root, timeout=15)
        merge_base = run_cmd(["git", "merge-base", "HEAD", baseline], cwd=root, timeout=10)

        git_info["changed_vs_baseline"] = parse_name_status(diff.stdout) if diff.returncode == 0 else []
        git_info["deleted_vs_baseline"] = sorted(normalize_rel(x) for x in deleted.stdout.splitlines() if x.strip()) if deleted.returncode == 0 else []
        git_info["shortstat_vs_baseline"] = shortstat.stdout.strip() if shortstat.returncode == 0 else None
        git_info["merge_base"] = merge_base.stdout.strip() if merge_base.returncode == 0 else None

        current_versions = extract_repo_version_values(root)
        baseline_versions = extract_repo_version_values(root, git_ref=baseline)
        regressions = []
        for key, current_value in current_versions.items():
            previous = baseline_versions.get(key)
            if isinstance(current_value, str) and isinstance(previous, str):
                cmp = compare_versions(current_value, previous)
                if cmp == -1:
                    regressions.append({"key": key, "current": current_value, "baseline": previous})
        git_info["version_values"] = current_versions
        git_info["baseline_version_values"] = baseline_versions
        git_info["version_regressions_vs_baseline"] = regressions
    else:
        git_info["version_values"] = extract_repo_version_values(root)

    log = run_cmd(
        ["git", "log", "-5", "--date=iso-strict", "--pretty=format:%h%x09%H%x09%ad%x09%an%x09%s"],
        cwd=root,
        timeout=10,
    )
    if log.returncode == 0:
        commits = []
        for line in log.stdout.splitlines():
            parts = line.split("\t", 4)
            if len(parts) == 5:
                commits.append(
                    {
                        "short": parts[0],
                        "hash": parts[1],
                        "date": parts[2],
                        "author": parts[3],
                        "subject": parts[4],
                    }
                )
        git_info["recent_commits"] = commits

    remotes = run_cmd(["git", "remote", "-v"], cwd=root, timeout=5)
    if remotes.returncode == 0:
        git_info["remotes"] = sorted(set(line.strip() for line in remotes.stdout.splitlines() if line.strip()))

    return git_info


def protected_path_snapshot(root: Path) -> dict[str, Any]:
    items: list[dict[str, Any]] = []
    missing: list[str] = []

    for rel in sorted(set(CRITICAL_PATHS + IMPORTANT_FILES)):
        path = root / rel
        item: dict[str, Any] = {"path": normalize_rel(rel), "exists": path.exists()}
        if not path.exists():
            missing.append(normalize_rel(rel))
        elif path.is_file():
            try:
                stat = path.stat()
                item.update(
                    {
                        "kind": "file",
                        "size_bytes": stat.st_size,
                        "sha256": sha256_file(path),
                    }
                )
            except OSError:
                item.update({"kind": "file", "size_bytes": None, "sha256": None})
        elif path.is_dir():
            counts = count_files(path)
            item.update({"kind": "directory", **counts})
        else:
            item["kind"] = "other"
        items.append(item)

    return {
        "missing": sorted(missing),
        "items": items,
    }


def parse_csproj(path: Path, root: Path) -> dict[str, Any]:
    text = read_text(path)
    target_framework = parse_xml_property(text, "TargetFramework")
    target_frameworks = parse_xml_property(text, "TargetFrameworks")
    is_test_project = parse_xml_property(text, "IsTestProject")
    package_refs = sorted(set(re.findall(r'<PackageReference\s+Include="([^"]+)"', text)))
    project_refs = sorted(normalize_rel(p) for p in re.findall(r'<ProjectReference\s+Include="([^"]+)"', text))
    return {
        "path": normalize_rel(path.relative_to(root)),
        "name": path.stem,
        "target_framework": target_framework,
        "target_frameworks": target_frameworks,
        "is_test_project": (is_test_project or "").lower() == "true" or path.stem.endswith("Tests"),
        "package_reference_count": len(package_refs),
        "project_reference_count": len(project_refs),
        "top_package_references": package_refs[:20],
    }


def dotnet_snapshot(root: Path, *, deep: bool = False) -> dict[str, Any]:
    info: dict[str, Any] = {
        "dotnet_available": command_exists("dotnet"),
        "global_json": read_json(root / "global.json"),
        "sdk_version_required": None,
        "sdk_version_active": None,
        "sdk_required_installed": None,
        "csproj_count": 0,
        "test_project_count": 0,
        "worker_project_count": 0,
        "command_center_project_count": 0,
        "projects": [],
        "solution_files": sorted(normalize_rel(p.relative_to(root)) for p in root.glob("*.sln*")),
    }

    sdk_required = info["global_json"].get("sdk", {}).get("version") if isinstance(info["global_json"], dict) else None
    info["sdk_version_required"] = sdk_required

    csprojs = sorted((root / "src").rglob("*.csproj")) if (root / "src").exists() else []
    projects = []
    for path in csprojs:
        rel_project = parse_csproj(path, root)
        projects.append(rel_project)

    info["projects"] = projects
    info["csproj_count"] = len(projects)
    info["test_project_count"] = sum(1 for p in projects if p["is_test_project"])
    info["worker_project_count"] = sum(1 for p in projects if ".Workers." in p["name"])
    info["command_center_project_count"] = sum(1 for p in projects if ".CommandCenter." in p["name"])

    if command_exists("dotnet"):
        version = run_cmd(["dotnet", "--version"], cwd=root, timeout=10)
        if version.returncode == 0:
            info["sdk_version_active"] = version.stdout.strip()

        sdks = run_cmd(["dotnet", "--list-sdks"], cwd=root, timeout=10)
        if sdks.returncode == 0:
            installed = [line.split()[0] for line in sdks.stdout.splitlines() if line.strip()]
            info["installed_sdks"] = installed
            if sdk_required:
                info["sdk_required_installed"] = sdk_required in installed

        if deep:
            restore_check = run_cmd(["dotnet", "restore", "ArgusEngine.slnx", "--locked-mode"], cwd=root, timeout=120)
            info["restore_locked_mode"] = restore_check.short(max_chars=2000)

    version_values = extract_repo_version_values(root)
    consistency: dict[str, Any] = {"values": version_values, "mismatches": []}
    root_version = version_values.get("VERSION")
    centralized = version_values.get("Directory.Build.targets:Version")
    if root_version and centralized and root_version != centralized:
        consistency["mismatches"].append(
            {
                "kind": "VERSION_vs_Directory.Build.targets:Version",
                "VERSION": root_version,
                "Directory.Build.targets:Version": centralized,
            }
        )
    info["version_consistency"] = consistency

    return info


def tokenize_service_catalog(text: str) -> list[str]:
    # Handles both proper TSV/space-delimited files and a flattened single-line file.
    return [token for token in re.split(r"[\t\r\n ]+", text.strip()) if token]


def parse_service_catalog(root: Path) -> dict[str, Any]:
    path = root / "deployment" / "service-catalog.tsv"
    text = read_text(path)
    result: dict[str, Any] = {
        "path": "deployment/service-catalog.tsv",
        "exists": path.exists(),
        "services": [],
        "service_count": 0,
        "errors": [],
        "missing_project_dirs": [],
        "missing_csproj": [],
        "missing_dockerfiles": [],
        "missing_extra_source_dirs": [],
        "ecr_enabled_count": 0,
        "kind_counts": {},
    }
    if not text.strip():
        if not path.exists():
            result["errors"].append("service-catalog.tsv is missing")
        else:
            result["errors"].append("service-catalog.tsv is empty")
        return result

    rows: list[dict[str, Any]] = []

    # Prefer line-aware parsing when the file has real rows.
    data_lines = [line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith("#")]
    if len(data_lines) >= 1:
        for line in data_lines:
            parts = line.split("\t") if "\t" in line else line.split()
            if len(parts) < 6:
                result["errors"].append(f"Could not parse service catalog row: {line[:120]}")
                continue
            service, project_dir, app_dll, dockerfile, ecr_enabled, kind = parts[:6]
            extra = parts[6] if len(parts) >= 7 else ""
            rows.append(
                {
                    "service": service,
                    "project_dir": project_dir,
                    "app_dll": app_dll,
                    "dockerfile": dockerfile,
                    "ecr_enabled": ecr_enabled == "1",
                    "kind": kind,
                    "extra_source_dirs": [normalize_rel(x) for x in extra.split(",") if x],
                }
            )
    else:
        tokens = tokenize_service_catalog(text)
        try:
            idx = tokens.index("extra_source_dirs") + 1
        except ValueError:
            idx = 0
        while idx + 5 < len(tokens):
            service, project_dir, app_dll, dockerfile, ecr_enabled, kind = tokens[idx : idx + 6]
            idx += 6
            extra = ""
            if idx < len(tokens) and ("/" in tokens[idx] or "," in tokens[idx]):
                extra = tokens[idx]
                idx += 1
            rows.append(
                {
                    "service": service,
                    "project_dir": project_dir,
                    "app_dll": app_dll,
                    "dockerfile": dockerfile,
                    "ecr_enabled": ecr_enabled == "1",
                    "kind": kind,
                    "extra_source_dirs": [normalize_rel(x) for x in extra.split(",") if x],
                }
            )

    for row in rows:
        project_dir = root / "src" / str(row["project_dir"])
        dockerfile = root / str(row["dockerfile"])
        csprojs = sorted(project_dir.glob("*.csproj")) if project_dir.exists() else []

        row["project_path"] = normalize_rel(project_dir.relative_to(root)) if project_dir.exists() else normalize_rel(project_dir.relative_to(root))
        row["project_dir_exists"] = project_dir.exists()
        row["csproj_exists"] = len(csprojs) > 0
        row["csproj_paths"] = [normalize_rel(p.relative_to(root)) for p in csprojs]
        row["dockerfile_exists"] = dockerfile.exists()

        missing_extra = []
        for extra in row["extra_source_dirs"]:
            if not (root / extra).exists():
                missing_extra.append(extra)
        row["missing_extra_source_dirs"] = sorted(missing_extra)

        if not row["project_dir_exists"]:
            result["missing_project_dirs"].append(str(row["project_dir"]))
        if not row["csproj_exists"]:
            result["missing_csproj"].append(str(row["project_dir"]))
        if not row["dockerfile_exists"]:
            result["missing_dockerfiles"].append(str(row["dockerfile"]))
        result["missing_extra_source_dirs"].extend(missing_extra)

    result["services"] = sorted(rows, key=lambda r: str(r["service"]))
    result["service_count"] = len(rows)
    result["ecr_enabled_count"] = sum(1 for row in rows if row.get("ecr_enabled"))
    kind_counts: dict[str, int] = {}
    for row in rows:
        kind = str(row.get("kind") or "unknown")
        kind_counts[kind] = kind_counts.get(kind, 0) + 1
    result["kind_counts"] = dict(sorted(kind_counts.items()))
    result["missing_project_dirs"] = sorted(set(result["missing_project_dirs"]))
    result["missing_csproj"] = sorted(set(result["missing_csproj"]))
    result["missing_dockerfiles"] = sorted(set(result["missing_dockerfiles"]))
    result["missing_extra_source_dirs"] = sorted(set(result["missing_extra_source_dirs"]))
    return result


def parse_compose_services_naive(path: Path) -> list[str]:
    text = read_text(path)
    if not text:
        return []
    services: list[str] = []
    in_services = False
    for raw in text.splitlines():
        line = raw.rstrip()
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if re.match(r"^services\s*:\s*$", stripped):
            in_services = True
            continue
        if in_services and line == stripped and re.match(r"^[A-Za-z0-9_.-]+\s*:\s*$", stripped):
            # New top-level section.
            break
        if in_services:
            match = re.match(r"^\s{2}([A-Za-z0-9_.-]+)\s*:\s*(?:#.*)?$", line)
            if match:
                services.append(match.group(1))
    return sorted(set(services))


def docker_compose_command() -> Optional[list[str]]:
    if command_exists("docker"):
        version = run_cmd(["docker", "compose", "version"], cwd=Path.cwd(), timeout=5)
        if version.returncode == 0:
            return ["docker", "compose"]
    if command_exists("docker-compose"):
        return ["docker-compose"]
    return None


def parse_json_lines_or_array(text: str) -> Any:
    stripped = text.strip()
    if not stripped:
        return None
    try:
        return json.loads(stripped)
    except json.JSONDecodeError:
        rows = []
        for line in stripped.splitlines():
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
        return rows if rows else None


def compose_snapshot(root: Path, *, runtime: bool = False) -> dict[str, Any]:
    compose_file = root / "deployment" / "docker-compose.yml"
    info: dict[str, Any] = {
        "compose_file": "deployment/docker-compose.yml",
        "exists": compose_file.exists(),
        "services_static": parse_compose_services_naive(compose_file),
        "services_from_docker_config": [],
        "docker_available": command_exists("docker"),
        "compose_available": False,
        "runtime_checked": runtime,
        "containers": [],
        "errors": [],
    }

    cmd = docker_compose_command()
    info["compose_available"] = cmd is not None
    if cmd and compose_file.exists():
        config_services = run_cmd(cmd + ["-f", str(compose_file), "config", "--services"], cwd=root, timeout=20)
        if config_services.returncode == 0:
            info["services_from_docker_config"] = sorted(x.strip() for x in config_services.stdout.splitlines() if x.strip())
        else:
            info["errors"].append(
                {
                    "command": "compose config --services",
                    "returncode": config_services.returncode,
                    "stderr": config_services.stderr.strip()[:1000],
                }
            )

        if runtime:
            ps = run_cmd(cmd + ["-f", str(compose_file), "ps", "--format", "json"], cwd=root, timeout=20)
            if ps.returncode == 0:
                parsed = parse_json_lines_or_array(ps.stdout)
                containers = parsed if isinstance(parsed, list) else ([parsed] if isinstance(parsed, dict) else [])
                normalized = []
                for item in containers:
                    if not isinstance(item, dict):
                        continue
                    normalized.append(
                        {
                            "name": item.get("Name") or item.get("name"),
                            "service": item.get("Service") or item.get("service"),
                            "state": item.get("State") or item.get("state"),
                            "health": item.get("Health") or item.get("health"),
                            "status": item.get("Status") or item.get("status"),
                            "publishers": item.get("Publishers") or item.get("publishers"),
                        }
                    )
                info["containers"] = sorted(normalized, key=lambda x: (str(x.get("service")), str(x.get("name"))))
            else:
                info["errors"].append(
                    {
                        "command": "compose ps --format json",
                        "returncode": ps.returncode,
                        "stderr": ps.stderr.strip()[:1000],
                    }
                )

    return info


def extract_python_literal_assignment(text: str, name: str) -> Optional[Any]:
    marker = f"{name} ="
    start = text.find(marker)
    if start < 0:
        return None
    brace_start = text.find("{", start)
    bracket_start = text.find("[", start)
    candidates = [i for i in [brace_start, bracket_start] if i >= 0]
    if not candidates:
        return None
    literal_start = min(candidates)
    opener = text[literal_start]
    closer = "}" if opener == "{" else "]"
    depth = 0
    in_string: Optional[str] = None
    escape = False
    for idx in range(literal_start, len(text)):
        ch = text[idx]
        if in_string:
            if escape:
                escape = False
            elif ch == "\\":
                escape = True
            elif ch == in_string:
                in_string = None
            continue
        if ch in ("'", '"'):
            in_string = ch
        elif ch == opener:
            depth += 1
        elif ch == closer:
            depth -= 1
            if depth == 0:
                snippet = text[literal_start : idx + 1]
                try:
                    return ast.literal_eval(snippet)
                except (SyntaxError, ValueError):
                    return None
    return None


def health_endpoints_from_deploy_py(root: Path) -> dict[str, str]:
    deploy_py = read_text(root / "deploy.py")
    parsed = extract_python_literal_assignment(deploy_py, "HEALTH_ENDPOINTS")
    if isinstance(parsed, dict):
        return {str(k): str(v) for k, v in sorted(parsed.items())}
    return dict(sorted(DEFAULT_HEALTH_ENDPOINTS.items()))


def probe_url(url: str, timeout: float) -> dict[str, Any]:
    started = time.monotonic()
    request = urllib.request.Request(url, method="GET", headers={"User-Agent": "argus-repo-health/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read(512).decode("utf-8", errors="replace")
            return {
                "url": url,
                "ok": 200 <= response.status < 400,
                "status_code": response.status,
                "duration_ms": int((time.monotonic() - started) * 1000),
                "body_prefix": body[:200],
                "error": None,
            }
    except urllib.error.HTTPError as ex:
        return {
            "url": url,
            "ok": False,
            "status_code": ex.code,
            "duration_ms": int((time.monotonic() - started) * 1000),
            "body_prefix": "",
            "error": str(ex),
        }
    except Exception as ex:  # network local probe; report exact local failure without crashing
        return {
            "url": url,
            "ok": False,
            "status_code": None,
            "duration_ms": int((time.monotonic() - started) * 1000),
            "body_prefix": "",
            "error": type(ex).__name__ + ": " + str(ex),
        }


def health_snapshot(root: Path, *, enabled: bool, expect_running: bool, timeout: float) -> dict[str, Any]:
    endpoints = health_endpoints_from_deploy_py(root)
    result: dict[str, Any] = {
        "enabled": enabled,
        "expect_running": expect_running,
        "endpoint_count": len(endpoints),
        "healthy_count": 0,
        "unhealthy_count": 0,
        "endpoints": {},
    }
    if not enabled:
        return result

    for service, url in sorted(endpoints.items()):
        probe = probe_url(url, timeout=timeout)
        result["endpoints"][service] = probe
    result["healthy_count"] = sum(1 for x in result["endpoints"].values() if x.get("ok"))
    result["unhealthy_count"] = sum(1 for x in result["endpoints"].values() if not x.get("ok"))
    return result


def workflow_snapshot(root: Path) -> dict[str, Any]:
    workflows_dir = root / ".github" / "workflows"
    workflows = []
    if workflows_dir.exists():
        for path in sorted(workflows_dir.glob("*.y*ml")):
            text = read_text(path)
            workflows.append(
                {
                    "path": normalize_rel(path.relative_to(root)),
                    "exists": True,
                    "sha256": sha256_file(path),
                    "mentions_dotnet_restore": "dotnet restore" in text,
                    "mentions_dotnet_build": "dotnet build" in text,
                    "mentions_tests": "test.sh" in text or "dotnet test" in text,
                    "mentions_docker": "docker" in text.lower(),
                    "mentions_deploy_validate": "deploy validate" in text or "./deploy validate" in text,
                }
            )
    return {
        "workflow_count": len(workflows),
        "workflows": workflows,
        "ci_exists": (root / ".github" / "workflows" / "ci.yml").exists(),
        "release_main_exists": (root / ".github" / "workflows" / "release-main.yml").exists(),
    }


def security_snapshot(root: Path, git_info: Mapping[str, Any]) -> dict[str, Any]:
    tracked_files: list[str] = []
    if command_exists("git"):
        ls = run_cmd(["git", "ls-files"], cwd=root, timeout=15)
        if ls.returncode == 0:
            tracked_files = [normalize_rel(x) for x in ls.stdout.splitlines() if x.strip()]

    tracked_secret_candidates = []
    for rel in tracked_files:
        if any(pattern.search(rel) for pattern in SECRET_FILE_PATTERNS):
            tracked_secret_candidates.append(rel)

    local_sensitive_files = []
    for pattern in [".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12"]:
        for path in root.glob(pattern):
            if path.is_file():
                local_sensitive_files.append(normalize_rel(path.relative_to(root)))
        for path in (root / "deployment").glob(pattern) if (root / "deployment").exists() else []:
            if path.is_file():
                local_sensitive_files.append(normalize_rel(path.relative_to(root)))

    env_presence = {key: bool(os.environ.get(key)) for key in SENSITIVE_ENV_KEYS}

    return {
        "tracked_secret_file_candidates": sorted(set(tracked_secret_candidates)),
        "local_sensitive_file_candidates": sorted(set(local_sensitive_files)),
        "sensitive_env_presence": dict(sorted(env_presence.items())),
    }


def cloud_snapshot(root: Path) -> dict[str, Any]:
    providers = {}
    for provider in ["aws", "azure", "gcp"]:
        path = root / "deployment" / provider
        files = []
        if path.exists():
            files = sorted(
                normalize_rel(p.relative_to(root))
                for p in path.rglob("*")
                if p.is_file() and ".terraform" not in p.parts
            )
        providers[provider] = {
            "path": f"deployment/{provider}",
            "exists": path.exists(),
            "file_count": len(files),
            "top_files": files[:50],
        }

    return {
        "providers": providers,
        "credential_env_presence": {
            "aws": bool(os.environ.get("AWS_ACCESS_KEY_ID") or os.environ.get("AWS_PROFILE")),
            "gcp": bool(os.environ.get("GOOGLE_APPLICATION_CREDENTIALS") or os.environ.get("GOOGLE_CLOUD_PROJECT")),
            "azure": bool(os.environ.get("AZURE_CLIENT_ID") or os.environ.get("AZURE_SUBSCRIPTION_ID")),
        },
    }


def classify_status_entries(entries: Sequence[Mapping[str, Any]]) -> dict[str, Any]:
    counts: dict[str, int] = {}
    deleted: list[str] = []
    untracked: list[str] = []
    modified: list[str] = []
    staged: list[str] = []

    for entry in entries:
        xy = str(entry.get("xy") or "")
        path = str(entry.get("path") or "")
        counts[xy] = counts.get(xy, 0) + 1
        if "D" in xy:
            deleted.append(path)
        if xy == "??":
            untracked.append(path)
        if xy[1:2] == "M" or xy[0:1] == "M":
            modified.append(path)
        if xy[0:1] not in (" ", "?"):
            staged.append(path)

    return {
        "counts_by_xy": dict(sorted(counts.items())),
        "deleted": sorted(deleted),
        "untracked": sorted(untracked),
        "modified": sorted(modified),
        "staged": sorted(staged),
    }


def critical_matches(paths: Iterable[str]) -> list[str]:
    matched = []
    for path in paths:
        rel = normalize_rel(path)
        if any(is_under(rel, critical) for critical in CRITICAL_PATHS):
            matched.append(rel)
    return sorted(set(matched))


def compare_catalog_to_compose(catalog: Mapping[str, Any], compose: Mapping[str, Any]) -> dict[str, Any]:
    catalog_services = sorted(str(row.get("service")) for row in catalog.get("services", []) if row.get("service"))
    compose_services = compose.get("services_from_docker_config") or compose.get("services_static") or []
    compose_services = sorted(str(x) for x in compose_services)

    catalog_set = set(catalog_services)
    compose_set = set(compose_services)

    infrastructure = {"postgres", "filestore-db-init", "redis", "rabbitmq"}
    expected_deployable = catalog_set
    compose_non_catalog = compose_set - catalog_set - infrastructure

    return {
        "catalog_services": catalog_services,
        "compose_services": compose_services,
        "catalog_not_in_compose": sorted(expected_deployable - compose_set),
        "compose_not_in_catalog_or_infra": sorted(compose_non_catalog),
        "infra_services_in_compose": sorted(compose_set & infrastructure),
    }


def issue(severity: str, code: str, message: str, evidence: Any = None, suggestion: Optional[str] = None) -> dict[str, Any]:
    item: dict[str, Any] = {
        "severity": severity,
        "code": code,
        "message": message,
    }
    if evidence is not None:
        item["evidence"] = evidence
    if suggestion:
        item["suggestion"] = suggestion
    return item


def build_issues(snapshot: Mapping[str, Any], *, expect_running: bool) -> list[dict[str, Any]]:
    issues: list[dict[str, Any]] = []

    git = snapshot.get("git", {})
    protected = snapshot.get("protected_paths", {})
    dotnet = snapshot.get("dotnet", {})
    catalog = snapshot.get("service_catalog", {})
    compose = snapshot.get("compose", {})
    health = snapshot.get("health", {})
    security = snapshot.get("security", {})
    workflows = snapshot.get("workflows", {})

    if git.get("available") is False:
        issues.append(issue("fail", "git_missing", "git is not available; repository integrity cannot be verified."))
    if git.get("errors"):
        issues.append(issue("fail", "git_error", "Git repository checks failed.", git.get("errors")))

    if git.get("is_clean") is False:
        classified = classify_status_entries(git.get("status_entries", []))
        issues.append(
            issue(
                "warn",
                "worktree_dirty",
                "Working tree has uncommitted changes.",
                {
                    "counts_by_xy": classified["counts_by_xy"],
                    "deleted": classified["deleted"][:25],
                    "modified": classified["modified"][:25],
                    "untracked": classified["untracked"][:25],
                },
                "Snapshot or commit intentional changes before destructive operations.",
            )
        )
        critical_deleted = critical_matches(classified["deleted"])
        if critical_deleted:
            issues.append(
                issue(
                    "fail",
                    "critical_worktree_deletion",
                    "Critical files or directories are currently deleted in the worktree.",
                    critical_deleted[:50],
                    "Recover or intentionally approve these deletions before asking an AI agent to deploy or refactor.",
                )
            )

    if git.get("behind"):
        issues.append(
            issue(
                "warn",
                "branch_behind_upstream",
                f"Current branch is behind upstream by {git.get('behind')} commit(s).",
                {"upstream": git.get("upstream")},
                "Pull/rebase before deployment unless intentionally testing an older revision.",
            )
        )

    deleted_vs_base = git.get("deleted_vs_baseline") or []
    critical_deleted_vs_base = critical_matches(deleted_vs_base)
    if critical_deleted_vs_base:
        issues.append(
            issue(
                "fail",
                "critical_deleted_vs_baseline",
                "Critical files were deleted compared with the selected baseline.",
                critical_deleted_vs_base[:100],
                "Verify this is intentional; otherwise restore from baseline.",
            )
        )

    regressions = git.get("version_regressions_vs_baseline") or []
    if regressions:
        issues.append(
            issue(
                "fail",
                "version_regression",
                "Version values appear to move backwards compared with baseline.",
                regressions,
                "Confirm the baseline and restore/increment version files.",
            )
        )

    missing = protected.get("missing") or []
    # Some cloud provider folders may not be present in every checkout, so make those warnings.
    truly_critical_missing = [
        p for p in missing
        if p not in {"deployment/aws", "deployment/azure", "deployment/gcp"}
    ]
    optional_cloud_missing = [p for p in missing if p in {"deployment/aws", "deployment/azure", "deployment/gcp"}]
    if truly_critical_missing:
        issues.append(
            issue(
                "fail",
                "missing_critical_paths",
                "Critical repository paths are missing.",
                truly_critical_missing[:100],
                "Restore missing paths before allowing mutation or deployment.",
            )
        )
    if optional_cloud_missing:
        issues.append(
            issue(
                "info",
                "missing_optional_cloud_paths",
                "Some cloud-provider deployment folders are absent.",
                optional_cloud_missing,
            )
        )

    if dotnet.get("dotnet_available") is False:
        issues.append(issue("warn", "dotnet_missing", "dotnet is not available on PATH; build/test readiness is unknown."))
    elif dotnet.get("sdk_version_required") and dotnet.get("sdk_required_installed") is False:
        issues.append(
            issue(
                "warn",
                "required_dotnet_sdk_missing",
                "The SDK version from global.json is not installed.",
                {
                    "required": dotnet.get("sdk_version_required"),
                    "active": dotnet.get("sdk_version_active"),
                    "installed_sdks": dotnet.get("installed_sdks", []),
                },
                "Install the pinned SDK or update global.json intentionally.",
            )
        )

    mismatches = dotnet.get("version_consistency", {}).get("mismatches", [])
    if mismatches:
        issues.append(
            issue(
                "fail",
                "version_file_mismatch",
                "Repository version files are inconsistent.",
                mismatches,
                "Keep VERSION and centralized MSBuild version properties in sync.",
            )
        )

    if catalog.get("exists") is False:
        issues.append(issue("fail", "service_catalog_missing", "deployment/service-catalog.tsv is missing."))
    if catalog.get("errors"):
        issues.append(issue("fail", "service_catalog_parse_error", "Service catalog could not be parsed cleanly.", catalog.get("errors")))
    for key, code, message in [
        ("missing_project_dirs", "catalog_missing_project_dirs", "Service catalog references missing project directories."),
        ("missing_csproj", "catalog_missing_csproj", "Service catalog references projects without .csproj files."),
        ("missing_dockerfiles", "catalog_missing_dockerfiles", "Service catalog references missing Dockerfiles."),
        ("missing_extra_source_dirs", "catalog_missing_extra_source_dirs", "Service catalog references missing extra source/resource directories."),
    ]:
        values = catalog.get(key) or []
        if values:
            issues.append(issue("fail", code, message, values[:100]))

    compare = snapshot.get("service_catalog_vs_compose", {})
    if compare.get("catalog_not_in_compose"):
        issues.append(
            issue(
                "warn",
                "catalog_services_missing_from_compose",
                "Some service-catalog services are not present in Docker Compose services.",
                compare.get("catalog_not_in_compose"),
            )
        )
    if compare.get("compose_not_in_catalog_or_infra"):
        issues.append(
            issue(
                "warn",
                "compose_services_not_in_catalog",
                "Docker Compose has services that are neither catalog services nor known infrastructure services.",
                compare.get("compose_not_in_catalog_or_infra"),
            )
        )

    if workflows.get("ci_exists") is False:
        issues.append(issue("warn", "ci_workflow_missing", ".github/workflows/ci.yml is missing."))
    if workflows.get("release_main_exists") is False:
        issues.append(issue("warn", "release_workflow_missing", ".github/workflows/release-main.yml is missing."))

    tracked_secret_candidates = security.get("tracked_secret_file_candidates") or []
    if tracked_secret_candidates:
        issues.append(
            issue(
                "fail",
                "tracked_secret_file_candidates",
                "Potential secret-bearing files are tracked by git.",
                tracked_secret_candidates,
                "Remove secrets from git history or verify these are safe examples.",
            )
        )

    if expect_running and health.get("enabled") and health.get("unhealthy_count", 0) > 0:
        issues.append(
            issue(
                "fail",
                "runtime_health_unhealthy",
                "One or more expected local runtime health endpoints are unhealthy.",
                {
                    service: {
                        "url": probe.get("url"),
                        "status_code": probe.get("status_code"),
                        "error": probe.get("error"),
                    }
                    for service, probe in (health.get("endpoints") or {}).items()
                    if not probe.get("ok")
                },
                "Inspect docker compose ps/logs or run ./deploy deploy --hot then ./deploy smoke.",
            )
        )
    elif health.get("enabled") and health.get("healthy_count", 0) == 0 and health.get("endpoint_count", 0) > 0:
        issues.append(
            issue(
                "info",
                "runtime_not_detected",
                "No local health endpoints responded successfully. This is expected if the stack is not running.",
            )
        )

    if compose.get("runtime_checked") and compose.get("containers"):
        bad_containers = [
            c for c in compose.get("containers", [])
            if str(c.get("state", "")).lower() not in {"running", "healthy"} and c.get("state") is not None
        ]
        if bad_containers:
            issues.append(issue("warn", "compose_containers_not_running", "Some compose containers are not running.", bad_containers[:50]))

    return sorted(issues, key=lambda x: ({"fail": 0, "warn": 1, "info": 2}.get(x["severity"], 9), x["code"]))


def summarize_changes(git: Mapping[str, Any]) -> dict[str, Any]:
    status_entries = git.get("status_entries") or []
    status_summary = classify_status_entries(status_entries)
    changed_vs_baseline = git.get("changed_vs_baseline") or []
    by_status: dict[str, int] = {}
    for item in changed_vs_baseline:
        s = str(item.get("status") or "?")
        key = s[0] if s else "?"
        by_status[key] = by_status.get(key, 0) + 1

    critical_changed = critical_matches([str(x.get("path")) for x in changed_vs_baseline if x.get("path")])
    return {
        "worktree": status_summary,
        "baseline_change_counts": dict(sorted(by_status.items())),
        "baseline_changed_files_count": len(changed_vs_baseline),
        "critical_changed_vs_baseline": critical_changed[:100],
        "deleted_vs_baseline_count": len(git.get("deleted_vs_baseline") or []),
    }


def summarize_recommended_actions(issues: Sequence[Mapping[str, Any]]) -> list[str]:
    actions: list[str] = []
    codes = {str(i.get("code")) for i in issues}

    if "critical_worktree_deletion" in codes or "critical_deleted_vs_baseline" in codes:
        actions.append("Do not deploy yet. Recover or explicitly approve critical deletions.")
    if "version_regression" in codes or "version_file_mismatch" in codes:
        actions.append("Fix version-file consistency before release/deployment.")
    if "service_catalog_missing" in codes or "service_catalog_parse_error" in codes or any(c.startswith("catalog_missing") for c in codes):
        actions.append("Fix deployment/service-catalog.tsv references before building images.")
    if "branch_behind_upstream" in codes:
        actions.append("Sync with upstream before cloud deployment unless intentionally testing older code.")
    if "worktree_dirty" in codes:
        actions.append("Create a snapshot/commit/stash before allowing the agent to mutate files.")
    if "runtime_health_unhealthy" in codes:
        actions.append("Inspect local compose logs and health endpoints before further automation.")
    if not actions:
        actions.append("Repo state has no blocking issues from this snapshot. Prefer read-only planning before mutation.")
    return actions


def score_and_status(issues: Sequence[Mapping[str, Any]]) -> dict[str, Any]:
    score = 100
    has_fail = False
    has_warn = False
    for item in issues:
        severity = item.get("severity")
        if severity == "fail":
            has_fail = True
            score -= 25
        elif severity == "warn":
            has_warn = True
            score -= 8
    score = max(0, min(100, score))
    status = "fail" if has_fail else ("warn" if has_warn else "ok")
    return {
        "status": status,
        "score": score,
        "fail_count": sum(1 for i in issues if i.get("severity") == "fail"),
        "warn_count": sum(1 for i in issues if i.get("severity") == "warn"),
        "info_count": sum(1 for i in issues if i.get("severity") == "info"),
    }


def system_snapshot(root: Path, *, stable: bool) -> dict[str, Any]:
    data: dict[str, Any] = {
        "platform": platform.platform(),
        "python_version": platform.python_version(),
        "tool_presence": {
            "git": command_exists("git"),
            "dotnet": command_exists("dotnet"),
            "docker": command_exists("docker"),
            "docker-compose": command_exists("docker-compose"),
            "bash": command_exists("bash"),
        },
    }
    if not stable:
        data["cwd"] = str(Path.cwd())
        data["repo_root"] = str(root)
    else:
        data["repo_root"] = "."
    return data


def build_snapshot(args: argparse.Namespace) -> dict[str, Any]:
    root = find_repo_root(Path(args.repo_root).resolve() if args.repo_root else None)
    if not root:
        raise RuntimeError("Could not find Argus Engine repository root. Run inside the checkout or pass --repo-root.")

    started = time.monotonic()

    git = git_snapshot(root, args.baseline_ref)
    protected = protected_path_snapshot(root)
    dotnet = dotnet_snapshot(root, deep=args.deep)
    catalog = parse_service_catalog(root)
    compose = compose_snapshot(root, runtime=args.runtime)
    health = health_snapshot(
        root,
        enabled=not args.skip_health,
        expect_running=args.expect_running,
        timeout=args.health_timeout,
    )
    workflows = workflow_snapshot(root)
    security = security_snapshot(root, git)
    cloud = cloud_snapshot(root)
    catalog_vs_compose = compare_catalog_to_compose(catalog, compose)

    snapshot: dict[str, Any] = {
        "schema": {
            "name": "argus_repo_health",
            "version": SCHEMA_VERSION,
        },
        "system": system_snapshot(root, stable=args.stable),
        "git": git,
        "changes": summarize_changes(git),
        "protected_paths": protected,
        "dotnet": dotnet,
        "service_catalog": catalog,
        "compose": compose,
        "service_catalog_vs_compose": catalog_vs_compose,
        "health": health,
        "workflows": workflows,
        "cloud": cloud,
        "security": security,
    }

    issues = build_issues(snapshot, expect_running=args.expect_running)
    overall = score_and_status(issues)
    snapshot["issues"] = issues
    snapshot["overall"] = overall
    snapshot["decision_context"] = {
        "top_risks": [i for i in issues if i.get("severity") in {"fail", "warn"}][:10],
        "recommended_next_actions": summarize_recommended_actions(issues),
        "safe_default_agent_mode": "read_only_plan" if overall["status"] != "ok" else "read_only_or_guarded_mutation",
        "mutation_gate": {
            "allow_file_mutation_without_snapshot": False,
            "allow_deploy_when_dirty": False,
            "allow_cloud_apply_without_explicit_approval": False,
            "allow_destructive_git_commands": False,
        },
    }

    if not args.stable:
        snapshot["generated_utc"] = utc_now_iso()
        snapshot["duration_ms"] = int((time.monotonic() - started) * 1000)

    return snapshot


def as_markdown(snapshot: Mapping[str, Any]) -> str:
    overall = snapshot.get("overall", {})
    git = snapshot.get("git", {})
    changes = snapshot.get("changes", {})
    dotnet = snapshot.get("dotnet", {})
    catalog = snapshot.get("service_catalog", {})
    compose = snapshot.get("compose", {})
    health = snapshot.get("health", {})
    issues = snapshot.get("issues", [])
    decision = snapshot.get("decision_context", {})

    lines: list[str] = []
    lines.append("# Argus Repo Health Snapshot")
    lines.append("")
    lines.append(f"- Overall: **{overall.get('status', 'unknown').upper()}** / score `{overall.get('score', '?')}`")
    if snapshot.get("generated_utc"):
        lines.append(f"- Generated UTC: `{snapshot.get('generated_utc')}`")
    lines.append(f"- Branch: `{git.get('branch') or 'unknown'}`")
    lines.append(f"- HEAD: `{git.get('head_short') or 'unknown'}`")
    lines.append(f"- Upstream: `{git.get('upstream') or 'none'}`; ahead `{git.get('ahead')}`; behind `{git.get('behind')}`")
    lines.append(f"- Worktree clean: `{git.get('is_clean')}`")
    lines.append(f"- Baseline: `{git.get('baseline_ref') or 'none'}`")
    lines.append("")
    lines.append("## Change Summary")
    lines.append("")
    lines.append(f"- Worktree deleted: `{len(changes.get('worktree', {}).get('deleted', []))}`")
    lines.append(f"- Worktree modified: `{len(changes.get('worktree', {}).get('modified', []))}`")
    lines.append(f"- Worktree untracked: `{len(changes.get('worktree', {}).get('untracked', []))}`")
    lines.append(f"- Files changed vs baseline: `{changes.get('baseline_changed_files_count')}`")
    lines.append(f"- Deleted vs baseline: `{changes.get('deleted_vs_baseline_count')}`")
    critical_changed = changes.get("critical_changed_vs_baseline") or []
    if critical_changed:
        lines.append("- Critical changed vs baseline:")
        for path in critical_changed[:20]:
            lines.append(f"  - `{path}`")
    lines.append("")
    lines.append("## Build / Runtime Context")
    lines.append("")
    lines.append(f"- Required .NET SDK: `{dotnet.get('sdk_version_required')}`")
    lines.append(f"- Active .NET SDK: `{dotnet.get('sdk_version_active')}`")
    lines.append(f"- SDK required installed: `{dotnet.get('sdk_required_installed')}`")
    lines.append(f"- Projects: `{dotnet.get('csproj_count')}` total, `{dotnet.get('test_project_count')}` test, `{dotnet.get('worker_project_count')}` worker, `{dotnet.get('command_center_project_count')}` command-center")
    lines.append(f"- Catalog services: `{catalog.get('service_count')}`; ECR-enabled `{catalog.get('ecr_enabled_count')}`")
    lines.append(f"- Compose services: `{len(compose.get('services_from_docker_config') or compose.get('services_static') or [])}`")
    if health.get("enabled"):
        lines.append(f"- Health endpoints: `{health.get('healthy_count')}` healthy / `{health.get('endpoint_count')}` total")
    else:
        lines.append("- Health endpoints: skipped")
    lines.append("")
    lines.append("## Issues")
    lines.append("")
    if not issues:
        lines.append("- No issues detected.")
    else:
        for item in issues[:30]:
            lines.append(f"- **{str(item.get('severity')).upper()}** `{item.get('code')}`: {item.get('message')}")
    lines.append("")
    lines.append("## Recommended Next Actions")
    lines.append("")
    for action in decision.get("recommended_next_actions", []):
        lines.append(f"- {action}")
    lines.append("")
    return "\n".join(lines)


def as_compact(snapshot: Mapping[str, Any]) -> str:
    # Compact output is intended to be pasted into an LLM context:
    # concise human summary first, then machine-readable decision payload.
    compact_payload = {
        "overall": snapshot.get("overall"),
        "git": {
            "branch": snapshot.get("git", {}).get("branch"),
            "head_short": snapshot.get("git", {}).get("head_short"),
            "upstream": snapshot.get("git", {}).get("upstream"),
            "ahead": snapshot.get("git", {}).get("ahead"),
            "behind": snapshot.get("git", {}).get("behind"),
            "is_clean": snapshot.get("git", {}).get("is_clean"),
            "baseline_ref": snapshot.get("git", {}).get("baseline_ref"),
        },
        "changes": snapshot.get("changes"),
        "versions": snapshot.get("git", {}).get("version_values"),
        "dotnet": {
            "required_sdk": snapshot.get("dotnet", {}).get("sdk_version_required"),
            "active_sdk": snapshot.get("dotnet", {}).get("sdk_version_active"),
            "required_sdk_installed": snapshot.get("dotnet", {}).get("sdk_required_installed"),
            "csproj_count": snapshot.get("dotnet", {}).get("csproj_count"),
            "test_project_count": snapshot.get("dotnet", {}).get("test_project_count"),
        },
        "services": {
            "catalog_count": snapshot.get("service_catalog", {}).get("service_count"),
            "catalog_kind_counts": snapshot.get("service_catalog", {}).get("kind_counts"),
            "compose_catalog_mismatch": snapshot.get("service_catalog_vs_compose"),
        },
        "health": {
            "enabled": snapshot.get("health", {}).get("enabled"),
            "expect_running": snapshot.get("health", {}).get("expect_running"),
            "healthy_count": snapshot.get("health", {}).get("healthy_count"),
            "unhealthy_count": snapshot.get("health", {}).get("unhealthy_count"),
        },
        "issues": snapshot.get("issues", [])[:20],
        "decision_context": snapshot.get("decision_context"),
    }
    return (
        as_markdown(snapshot)
        + "\n---\n"
        + "```json\n"
        + json.dumps(compact_payload, indent=2, sort_keys=True)
        + "\n```\n"
    )


def write_output(text: str, output_path: Optional[str]) -> None:
    if output_path:
        path = Path(output_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    else:
        print(text)


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Create a deterministic read-only health/state snapshot for Argus Engine AI agents."
    )
    parser.add_argument("--repo-root", help="Path to repository root. Defaults to auto-detect from current directory.")
    parser.add_argument("--baseline-ref", help="Git ref to compare against. Defaults to upstream, origin/main, then main.")
    parser.add_argument(
        "--format",
        choices=["json", "markdown", "compact"],
        default="json",
        help="Output format. json is best for tool ingestion; compact is best for LLM context.",
    )
    parser.add_argument("--output", help="Write output to this file instead of stdout.")
    parser.add_argument(
        "--runtime",
        action="store_true",
        help="Also inspect docker compose runtime state with `docker compose ps`.",
    )
    parser.add_argument(
        "--expect-running",
        action="store_true",
        help="Treat unhealthy local health endpoints as failures. Without this, stopped local stack is informational.",
    )
    parser.add_argument(
        "--skip-health",
        action="store_true",
        help="Skip local HTTP health endpoint probes.",
    )
    parser.add_argument(
        "--health-timeout",
        type=float,
        default=0.75,
        help="Timeout in seconds for each local health endpoint probe.",
    )
    parser.add_argument(
        "--deep",
        action="store_true",
        help="Run deeper read-only checks such as dotnet restore --locked-mode. May be slower and may create normal SDK cache/obj artifacts.",
    )
    parser.add_argument(
        "--stable",
        action="store_true",
        help="Omit volatile timestamp/duration/absolute-path fields for easier diffing.",
    )
    parser.add_argument(
        "--exit-nonzero-on-warn",
        action="store_true",
        help="Return exit code 1 when warnings are present. Failures always return 2.",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str]) -> int:
    args = parse_args(argv)
    try:
        snapshot = build_snapshot(args)
    except Exception as ex:
        error_payload = {
            "schema": {"name": "argus_repo_health", "version": SCHEMA_VERSION},
            "overall": {"status": "fail", "score": 0, "fail_count": 1, "warn_count": 0, "info_count": 0},
            "issues": [
                {
                    "severity": "fail",
                    "code": "snapshot_script_error",
                    "message": str(ex),
                }
            ],
        }
        text = json.dumps(error_payload, indent=2, sort_keys=True)
        write_output(text, args.output if hasattr(args, "output") else None)
        return 3

    if args.format == "json":
        text = json.dumps(snapshot, indent=2, sort_keys=True)
    elif args.format == "markdown":
        text = as_markdown(snapshot)
    else:
        text = as_compact(snapshot)

    write_output(text, args.output)

    status = snapshot.get("overall", {}).get("status")
    if status == "fail":
        return 2
    if status == "warn" and args.exit_nonzero_on_warn:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
