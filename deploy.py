#!/usr/bin/env python3
"""
Argus Engine deployment console.

This script intentionally uses only the Python standard library so it can run on
fresh EC2/local hosts before project dependencies are installed.

It is both:
  1. a friendly menu-driven CLI for humans, and
  2. the single Python source of truth for deployment operations.

Examples:
    ./deploy
    ./deploy all-in-1
    ./deploy deploy --hot
    ./deploy deploy --image command-center-web worker-spider
    ./deploy scale local worker-spider=4 worker-enum=2
    ./deploy scale ecs worker-spider=6 worker-techid=1
    ./deploy monitor
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Optional, Sequence


# Keep this list in sync with deployment/docker-compose.yml. The script will prefer
# `docker compose config --services` when Docker is available.
FALLBACK_SERVICES = [
    "postgres",
    "filestore-db-init",
    "redis",
    "rabbitmq",
    "command-center-gateway",
    "command-center-operations-api",
    "command-center-discovery-api",
    "command-center-worker-control-api",
    "command-center-maintenance-api",
    "command-center-updates-api",
    "command-center-realtime",
    "command-center-cloud-deploy-api",
    "command-center-bootstrapper",
    "command-center-spider-dispatcher",
    "command-center-web",
    "gatekeeper",
    "worker-spider",
    "worker-http-requester",
    "worker-enum",
    "worker-portscan",
    "worker-highvalue",
    "worker-techid",
]

INFRASTRUCTURE_SERVICES = [
    "postgres",
    "filestore-db-init",
    "redis",
    "rabbitmq",
]

WEB_APP_SERVICES = [
    "command-center-web",
]

WORKERS = {
    "worker-spider": {
        "label": "Spider worker",
        "short": "spider",
        "local_env": "ARGUS_WORKER_SPIDER_REPLICAS",
        "legacy_flag": "--scale-spider",
        "ecs_env": "WORKER_SPIDER_SERVICE",
        "ecs_default": "argus-worker-spider",
        "desired_env": "ECS_DESIRED_WORKER_SPIDER",
        "description": "Discovery/spider jobs and HTTP queue intake.",
    },
    "worker-http-requester": {
        "label": "HTTP requester worker",
        "short": "http",
        "local_env": "ARGUS_WORKER_HTTP_REQUESTER_REPLICAS",
        "legacy_flag": "--scale-http-requester",
        "ecs_env": "WORKER_HTTP_REQUESTER_SERVICE",
        "ecs_default": "argus-worker-http-requester",
        "desired_env": "ECS_DESIRED_WORKER_HTTP_REQUESTER",
        "description": "Distributed HTTP request processing.",
    },
    "worker-enum": {
        "label": "Enumeration worker",
        "short": "enum",
        "local_env": "ARGUS_WORKER_ENUM_REPLICAS",
        "legacy_flag": "--scale-enum",
        "ecs_env": "WORKER_ENUM_SERVICE",
        "ecs_default": "argus-worker-enum",
        "desired_env": "ECS_DESIRED_WORKER_ENUM",
        "description": "Subdomain enumeration using bundled recon tools.",
    },
    "worker-portscan": {
        "label": "Port scan worker",
        "short": "portscan",
        "local_env": "ARGUS_WORKER_PORTSCAN_REPLICAS",
        "legacy_flag": "--scale-portscan",
        "ecs_env": "WORKER_PORTSCAN_SERVICE",
        "ecs_default": "argus-worker-portscan",
        "desired_env": "ECS_DESIRED_WORKER_PORTSCAN",
        "description": "Port scanning pipeline.",
    },
    "worker-highvalue": {
        "label": "High-value worker",
        "short": "highvalue",
        "local_env": "ARGUS_WORKER_HIGHVALUE_REPLICAS",
        "legacy_flag": "--scale-highvalue",
        "ecs_env": "WORKER_HIGHVALUE_SERVICE",
        "ecs_default": "argus-worker-highvalue",
        "desired_env": "ECS_DESIRED_WORKER_HIGHVALUE",
        "description": "High-value path/regex analysis.",
    },
    "worker-techid": {
        "label": "Technology identification worker",
        "short": "techid",
        "local_env": "ARGUS_WORKER_TECHID_REPLICAS",
        "legacy_flag": "--scale-techid",
        "ecs_env": "WORKER_TECHID_SERVICE",
        "ecs_default": "argus-worker-techid",
        "desired_env": "ECS_DESIRED_WORKER_TECHID",
        "description": "Technology fingerprinting.",
    },
}

HEALTH_ENDPOINTS = {
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

PROJECT_HINTS = {
    "command-center-gateway": ["src/ArgusEngine.CommandCenter.Gateway/"],
    "command-center-operations-api": ["src/ArgusEngine.CommandCenter.Operations.Api/"],
    "command-center-discovery-api": ["src/ArgusEngine.CommandCenter.Discovery.Api/"],
    "command-center-worker-control-api": ["src/ArgusEngine.CommandCenter.WorkerControl.Api/"],
    "command-center-maintenance-api": ["src/ArgusEngine.CommandCenter.Maintenance.Api/"],
    "command-center-updates-api": ["src/ArgusEngine.CommandCenter.Updates.Api/"],
    "command-center-realtime": ["src/ArgusEngine.CommandCenter.Realtime.Host/"],
    "command-center-cloud-deploy-api": ["src/ArgusEngine.CommandCenter.CloudDeploy.Api/"],
    "command-center-bootstrapper": ["src/ArgusEngine.CommandCenter.Bootstrapper/"],
    "command-center-spider-dispatcher": ["src/ArgusEngine.CommandCenter.SpiderDispatcher/"],
    "command-center-web": ["src/ArgusEngine.CommandCenter.Web/"],
    "gatekeeper": ["src/ArgusEngine.Gatekeeper/"],
    "worker-spider": ["src/ArgusEngine.Workers.Spider/"],
    "worker-http-requester": ["src/ArgusEngine.Workers.HttpRequester/"],
    "worker-enum": ["src/ArgusEngine.Workers.Enumeration/"],
    "worker-portscan": ["src/ArgusEngine.Workers.PortScan/"],
    "worker-highvalue": ["src/ArgusEngine.Workers.HighValue/"],
    "worker-techid": ["src/ArgusEngine.Workers.TechnologyIdentification/"],
}

GLOBAL_INVALIDATORS = {
    "ArgusEngine.slnx",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    ".dockerignore",
    "deployment/docker-compose.yml",
    "deployment/Dockerfile.base-runtime",
    "deployment/Dockerfile.base-recon",
    "deployment/Dockerfile.commandcenter-host",
    "deployment/Dockerfile.worker",
    "deployment/Dockerfile.worker-enum",
}

GLOBAL_BUILD_INPUTS = {
    "ArgusEngine.slnx",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.config",
    "global.json",
    ".dockerignore",
    "deployment/service-catalog.tsv",
    "deploy.py",
}

DOCKERFILE_RESOURCE_HINTS = {
    "deployment/Dockerfile.base-runtime": "all",
    "deployment/Dockerfile.base-recon": {"worker-enum"},
    "deployment/wordlists": {"worker-enum"},
    "deployment/artifacts/recon-tools": {"worker-enum"},
}

GCP_WORKER_SERVICES = [
    "worker-spider",
    "worker-http-requester",
    "worker-enum",
    "worker-portscan",
    "worker-highvalue",
    "worker-techid",
]

GCP_SERVICE_PROJECT_DIR = {
    "worker-spider": "ArgusEngine.Workers.Spider",
    "worker-http-requester": "ArgusEngine.Workers.HttpRequester",
    "worker-enum": "ArgusEngine.Workers.Enumeration",
    "worker-portscan": "ArgusEngine.Workers.PortScan",
    "worker-highvalue": "ArgusEngine.Workers.HighValue",
    "worker-techid": "ArgusEngine.Workers.TechnologyIdentification",
}

GCP_SERVICE_APP_DLL = {
    "worker-spider": "ArgusEngine.Workers.Spider.dll",
    "worker-http-requester": "ArgusEngine.Workers.HttpRequester.dll",
    "worker-enum": "ArgusEngine.Workers.Enumeration.dll",
    "worker-portscan": "ArgusEngine.Workers.PortScan.dll",
    "worker-highvalue": "ArgusEngine.Workers.HighValue.dll",
    "worker-techid": "ArgusEngine.Workers.TechnologyIdentification.dll",
}


@dataclass(frozen=True)
class Options:
    dry_run: bool = False
    assume_yes: bool = False
    no_color: bool = False
    repo_root: Optional[Path] = None
    verbose: bool = False


@dataclass(frozen=True)
class Paths:
    repo_root: Path
    deploy_dir: Path
    compose_file: Path
    aws_dir: Path

    @staticmethod
    def resolve(explicit: Optional[Path] = None) -> "Paths":
        start = explicit.resolve() if explicit else Path.cwd().resolve()
        if start.is_file():
            start = start.parent

        for candidate in [start, *start.parents]:
            deploy_dir = candidate / "deployment"
            compose_file = deploy_dir / "docker-compose.yml"
            if deploy_dir.is_dir() and compose_file.exists():
                return Paths(
                    repo_root=candidate,
                    deploy_dir=deploy_dir,
                    compose_file=compose_file,
                    aws_dir=deploy_dir / "aws",
                )

        raise SystemExit(
            "Could not find the Argus Engine repository root. "
            "Run this from inside the argus-engine checkout or pass --repo-root PATH."
        )

    def rel(self, path: Path) -> str:
        try:
            return str(path.resolve().relative_to(self.repo_root.resolve()))
        except ValueError:
            return str(path)


class Ui:
    def __init__(self, *, no_color: bool = False):
        self.use_color = sys.stdout.isatty() and not no_color and "NO_COLOR" not in os.environ

    def color(self, text: str, code: str) -> str:
        if not self.use_color:
            return text
        return f"\033[{code}m{text}\033[0m"

    def header(self, title: str) -> None:
        print()
        print(self.color(f"╭─ {title}", "1;36"))
        print(self.color("╰" + "─" * (len(title) + 2), "36"))

    def section(self, title: str) -> None:
        print()
        print(self.color(title, "1;34"))
        print(self.color("-" * len(title), "34"))

    def info(self, text: str) -> None:
        print(f"{self.color('ℹ', '36')} {text}")

    def ok(self, text: str) -> None:
        print(f"{self.color('✓', '32')} {text}")

    def warn(self, text: str) -> None:
        print(f"{self.color('!', '33')} {text}")

    def error(self, text: str) -> None:
        print(f"{self.color('✗', '31')} {text}", file=sys.stderr)

    def command(self, args: Sequence[str]) -> None:
        print(self.color("$ " + " ".join(shlex.quote(a) for a in args), "2"))

    def prompt(self, label: str, default: Optional[str] = None) -> str:
        suffix = f" [{default}]" if default not in (None, "") else ""
        value = input(f"{label}{suffix}: ").strip()
        if not value and default is not None:
            return default
        return value

    def prompt_int(self, label: str, default: int, minimum: int = 0, maximum: Optional[int] = None) -> int:
        while True:
            raw = self.prompt(label, str(default))
            try:
                value = int(raw)
            except ValueError:
                self.warn("Enter a whole number.")
                continue
            if value < minimum:
                self.warn(f"Enter a value >= {minimum}.")
                continue
            if maximum is not None and value > maximum:
                self.warn(f"Enter a value <= {maximum}.")
                continue
            return value

    def confirm(self, label: str, *, default: bool = False, assume_yes: bool = False) -> bool:
        if assume_yes:
            self.ok(f"{label} yes")
            return True
        suffix = "Y/n" if default else "y/N"
        raw = input(f"{label} [{suffix}]: ").strip().lower()
        if not raw:
            return default
        return raw in {"y", "yes"}

    def choose(self, title: str, choices: Sequence[str], *, default: int = 1) -> int:
        while True:
            print()
            self.info(title)
            for i, choice in enumerate(choices, 1):
                print(f"  {i:2}. {choice}")
            raw = self.prompt("Choose", str(default))
            try:
                idx = int(raw)
            except ValueError:
                self.warn("Enter a menu number.")
                continue
            if 1 <= idx <= len(choices):
                return idx - 1
            self.warn(f"Choose between 1 and {len(choices)}.")

    def pause(self) -> None:
        if sys.stdin.isatty():
            input("\nPress Enter to continue...")


class Runner:
    def __init__(self, paths: Paths, options: Options, ui: Ui, env: Mapping[str, str]):
        self.paths = paths
        self.options = options
        self.ui = ui
        self.env = dict(env)

    def merged_env(self, extra: Optional[Mapping[str, str]] = None) -> dict[str, str]:
        env = dict(os.environ)
        env.update(self.env)
        env["ARGUS_NO_UI"] = "1"
        if extra:
            env.update({k: str(v) for k, v in extra.items()})
        return env

    def run(
        self,
        args: Sequence[str],
        *,
        env: Optional[Mapping[str, str]] = None,
        check: bool = False,
    ) -> int:
        self.ui.command(args)
        if self.options.dry_run:
            return 0
        try:
            completed = subprocess.run(
                list(args),
                cwd=self.paths.repo_root,
                env=self.merged_env(env),
                check=False,
            )
        except FileNotFoundError:
            self.ui.error(f"Command not found: {args[0]}")
            return 127
        if check and completed.returncode != 0:
            raise SystemExit(completed.returncode)
        return completed.returncode

    def capture(self, args: Sequence[str], *, env: Optional[Mapping[str, str]] = None) -> tuple[int, str, str]:
        if self.options.verbose:
            self.ui.command(args)
        if self.options.dry_run:
            return 0, "", ""
        try:
            completed = subprocess.run(
                list(args),
                cwd=self.paths.repo_root,
                env=self.merged_env(env),
                check=False,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            return completed.returncode, completed.stdout, completed.stderr
        except FileNotFoundError:
            return 127, "", f"Command not found: {args[0]}"


def parse_env_file(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.exists():
        return values
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return values

    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[len("export ") :].strip()
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key:
            values[key] = value
    return values


def load_environment(paths: Paths) -> dict[str, str]:
    merged: dict[str, str] = {}
    for path in [
        paths.deploy_dir / ".env",
        paths.deploy_dir / ".env.local",
        paths.deploy_dir / "gcp" / ".env",
        paths.aws_dir / ".env",
        paths.aws_dir / ".env.generated",
    ]:
        for key, value in parse_env_file(path).items():
            merged.setdefault(key, value)
    return merged


def which(program: str) -> Optional[str]:
    return shutil.which(program)


class ArgusDeployConsole:
    def __init__(self, paths: Paths, options: Options):
        self.paths = paths
        self.options = options
        self.ui = Ui(no_color=options.no_color)
        self.env = load_environment(paths)
        self.runner = Runner(paths, options, self.ui, self.env)
        self._compose_cmd: Optional[list[str]] = None
        self._services: Optional[list[str]] = None
        self._service_catalog_cache: Optional[dict[str, dict[str, object]]] = None
        self._service_source_paths_cache: dict[str, list[str]] = {}
        self._service_signature_cache: dict[tuple[str, str], dict[str, str]] = {}

    # ---------- command discovery ----------

    def compose_cmd(self) -> list[str]:
        if self._compose_cmd is not None:
            return list(self._compose_cmd)

        code, _, _ = self.runner.capture(["docker", "compose", "version"])
        if code == 0:
            self._compose_cmd = ["docker", "compose"]
            return list(self._compose_cmd)

        if which("docker-compose"):
            self._compose_cmd = ["docker-compose"]
            return list(self._compose_cmd)

        self.ui.error("Docker Compose was not found. Install Docker Compose v2 and rerun deploy.py.")
        self._compose_cmd = ["docker", "compose"]
        return list(self._compose_cmd)

    def compose(self, *args: str) -> list[str]:
        return [*self.compose_cmd(), "-f", str(self.paths.compose_file), *args]

    def services(self) -> list[str]:
        if self._services is not None:
            return list(self._services)

        code, stdout, _ = self.runner.capture(self.compose("config", "--services"))
        services = [line.strip() for line in stdout.splitlines() if line.strip()] if code == 0 else []
        if not services:
            services = list(FALLBACK_SERVICES)
        self._services = services
        return list(services)

    def app_services(self) -> list[str]:
        infrastructure = {"postgres", "filestore-db-init", "redis", "rabbitmq"}
        return [s for s in self.services() if s not in infrastructure]

    def worker_services(self) -> list[str]:
        available = set(self.services())
        return [service for service in WORKERS if service in available]

    def project_services(self) -> list[str]:
        workers = set(self.worker_services())
        return [service for service in self.app_services() if service not in workers]

    # ---------- top-level dispatch ----------

    def run(self, argv: Sequence[str]) -> int:
        if not argv or argv[0].lower() in {"menu", "ui", "interactive"}:
            return self.menu()

        command = argv[0].lower()
        rest = list(argv[1:])

        # Keep the historical CLI shortcuts working without delegating to any repository script.
        if command in {"up"}:
            return self.deploy_from_args(rest)
        if command in {"--fresh", "-fresh"}:
            return self.compose_action(["--fresh", *rest])
        if command in {"--ecs-workers"}:
            return self.compose_action(["--ecs-workers", *rest])
        if command in {"--gcp-workers", "--google-workers"}:
            return self.gcp_from_args(["release", *rest])
        if command in {"--hot", "-hot"}:
            return self.compose_action(["--hot", *rest])
        if command in {"--image", "-image"}:
            return self.compose_action(["--image", *rest])
        if command in {"--fast", "-fast"}:
            return self.all_in_one_deploy(rest)

        if command in {"deploy", "update"}:
            return self.deploy_from_args(rest)
        if command in {"all-in-1", "all-in-one", "allin1", "allinone", "auto", "auto-deploy"}:
            return self.all_in_one_deploy(rest)
        if command == "preflight":
            return self.preflight()
        if command in {"scale", "workers", "worker"}:
            return self.scale_from_args(rest)
        if command in {"monitor", "status", "ps"}:
            return self.monitor_from_args([command, *rest])
        if command in {"logs", "log"}:
            return self.logs_from_args(rest)
        if command == "restart":
            return self.compose_action(["restart", *rest])
        if command == "down":
            return self.compose_action(["down", *rest])
        if command == "smoke":
            return self.compose_action(["smoke", *rest])
        if command in {"validate", "manifests"}:
            return self.validate_manifests(rest)
        if command == "clean":
            return self.clean()
        if command in {"ecs", "aws", "cloud"}:
            return self.ecs_from_args(rest)
        if command in {"gcp", "google"}:
            return self.gcp_from_args(rest)
        if command in {"services", "components"}:
            self.show_services()
            return 0
        if command in {"health", "check"}:
            return self.health_checks()
        if command in {"changed", "affected"}:
            self.show_changed_services()
            return 0
        if command in {"help", "-h", "--help"}:
            self.print_help()
            return 0

        self.ui.error(f"Unknown command: {argv[0]}")
        self.print_help()
        return 2

    # ---------- menus ----------

    def menu(self) -> int:
        while True:
            self.ui.header("Argus Engine deployment console")
            self.show_context(compact=True)

            choice = self.ui.choose(
                "Choose a deployment task",
                [
                    "All-in-1 fastest smart deploy from GitHub",
                    "Deploy Web App",
                    "Deploy Infrastructure",
                    "Deploy All Workers",
                    "Deploy Select Workers",
                    "Deploy Select Projects",
                    "Rebuild All Images",
                    "Rebuild Select Images",
                    "Monitor / Operations",
                    "Exit",
                ],
            )

            if choice == 0:
                code = self.all_in_one_deploy([])
            elif choice == 1:
                code = self.deploy_web_app()
            elif choice == 2:
                code = self.deploy_infrastructure()
            elif choice == 3:
                code = self.deploy_all_workers()
            elif choice == 4:
                code = self.deploy_selected_workers()
            elif choice == 5:
                code = self.deploy_selected_projects()
            elif choice == 6:
                code = self.rebuild_all_images()
            elif choice == 7:
                code = self.rebuild_selected_images()
            elif choice == 8:
                code = self.monitor_operations_menu()
            else:
                return 0

            if code != 0:
                self.ui.warn(f"Last action exited with code {code}.")
            self.ui.pause()

    def deploy_web_app(self) -> int:
        services = [service for service in WEB_APP_SERVICES if service in self.services()]
        if not services:
            self.ui.error("command-center-web is not present in the Compose service list.")
            return 2
        self.ui.info("Building and deploying the Command Center web app.")
        return self.compose_action(["--image", *services])

    def deploy_infrastructure(self) -> int:
        services = [service for service in INFRASTRUCTURE_SERVICES if service in self.services()]
        if not services:
            self.ui.error("No infrastructure services were found in the Compose service list.")
            return 2
        self.ui.info("Starting infrastructure dependencies: " + ", ".join(services))
        return self.runner.run(self.compose("up", "-d", *services))

    def deploy_all_workers(self) -> int:
        workers = [worker for worker in self.worker_services() if worker in GCP_WORKER_SERVICES]
        return self.deploy_workers_to_target(workers, title="Deploy all workers")

    def deploy_selected_workers(self) -> int:
        workers = self.select_services(include_infra=False, only_cloudish=True, default="workers")
        workers = [worker for worker in workers if worker in WORKERS]
        if not workers:
            self.ui.warn("No workers selected.")
            return 0
        return self.deploy_workers_to_target(workers, title="Deploy selected workers")

    def deploy_workers_to_target(self, workers: Sequence[str], *, title: str) -> int:
        workers = [worker for worker in workers if worker in WORKERS]
        if not workers:
            self.ui.warn("No workers are available to deploy.")
            return 0

        choice = self.ui.choose(
            title,
            [
                "Google Cloud Run - build, push, and deploy workers",
                "Local Docker Compose - build images and restart workers",
                "Local Docker Compose - restart workers without rebuild",
                "Back",
            ],
        )
        if choice == 0:
            return self.gcp_from_args(["release", *workers])
        if choice == 1:
            return self.compose_action(["--image", *workers])
        if choice == 2:
            return self.runner.run(self.compose("up", "-d", "--no-deps", *workers))
        return 0

    def deploy_selected_projects(self) -> int:
        services = self.select_project_services(default="changed")
        if not services:
            self.ui.warn("No projects selected.")
            return 0

        choice = self.ui.choose(
            "Deploy selected projects",
            [
                "Build images and deploy selected projects",
                "Deploy selected projects without rebuilding",
                "Restart selected projects",
                "Back",
            ],
        )
        if choice == 0:
            return self.compose_action(["--image", *services])
        if choice == 1:
            return self.runner.run(self.compose("up", "-d", "--no-deps", *services))
        if choice == 2:
            return self.compose_action(["restart", *services])
        return 0

    def rebuild_all_images(self) -> int:
        self.ui.info("Rebuilding every Compose image without starting containers.")
        return self.runner.run(self.compose("build"))

    def rebuild_selected_images(self) -> int:
        services = self.select_services(include_infra=False, allow_empty=False, default="changed")
        if not services:
            self.ui.warn("No services selected.")
            return 0
        return self.runner.run(self.compose("build", *services))

    def monitor_operations_menu(self) -> int:
        choice = self.ui.choose(
            "Monitor / operations",
            [
                "Status",
                "Health checks",
                "Logs",
                "Worker counts",
                "Scale workers",
                "Service operations",
                "Google Cloud worker operations",
                "AWS ECS / ECR operations",
                "Show changed/affected services",
                "Back",
            ],
        )
        if choice == 0:
            return self.compose_action(["status"])
        if choice == 1:
            return self.health_checks()
        if choice == 2:
            return self.monitor_menu()
        if choice == 3:
            self.show_worker_counts()
            return 0
        if choice == 4:
            return self.scale_menu()
        if choice == 5:
            return self.operations_menu()
        if choice == 6:
            return self.gcp_menu()
        if choice == 7:
            return self.ecs_menu()
        if choice == 8:
            self.show_changed_services()
            return 0
        return 0

    def deploy_menu(self) -> int:
        choice = self.ui.choose(
            "Deploy/update",
            [
                "Incremental hot deploy — fastest for source-only changes",
                "Incremental image deploy — rebuild changed images",
                "Full fresh rebuild — no-cache image rebuild and recreate",
                "Deploy/update selected components",
                "Deploy local core + Google Cloud Run workers",
                "Deploy local core + ECS workers from this EC2 host",
                "Back",
            ],
        )
        if choice == 0:
            return self.compose_action(["--hot"])
        if choice == 1:
            return self.compose_action(["--image"])
        if choice == 2:
            return self.compose_action(["--fresh"])
        if choice == 3:
            return self.selected_component_deploy()
        if choice == 4:
            return self.gcp_from_args(["release"])
        if choice == 5:
            return self.compose_action(["--ecs-workers"])
        return 0

    def selected_component_deploy(self) -> int:
        services = self.select_services(include_infra=False, default="changed")
        if not services:
            self.ui.warn("No components selected.")
            return 0

        mode = self.ui.choose(
            "Selected component action",
            [
                "Build images, then up -d selected components",
                "Up -d selected components without rebuilding",
                "Force recreate selected components without rebuilding",
                "Restart selected components only",
                "Tail logs for selected components",
            ],
        )

        if mode == 0:
            build = self.runner.run(self.compose("build", *services))
            if build != 0:
                return build
            return self.runner.run(self.compose("up", "-d", "--no-deps", *services))
        if mode == 1:
            return self.runner.run(self.compose("up", "-d", "--no-deps", *services))
        if mode == 2:
            return self.runner.run(self.compose("up", "-d", "--no-deps", "--force-recreate", *services))
        if mode == 3:
            return self.compose_action(["restart", *services])
        return self.compose_action(["logs", "--tail", "200", *services])

    def scale_menu(self) -> int:
        choice = self.ui.choose(
            "Worker scaling",
            [
                "Show current local worker replica counts",
                "Scale all local Docker Compose workers",
                "Scale one local Docker Compose worker",
                "Run queue-driven ECS autoscaler once",
                "Manually set ECS desired worker counts",
                "Show ECS worker/service status",
                "Back",
            ],
        )
        if choice == 0:
            self.show_worker_counts()
            return 0
        if choice == 1:
            counts = self.prompt_worker_counts()
            return self.apply_local_worker_scale(counts)
        if choice == 2:
            selected = self.select_worker()
            current = self.local_service_count(selected)
            count = self.ui.prompt_int(f"{selected} replicas", current, minimum=0)
            return self.apply_local_worker_scale({selected: count})
        if choice == 3:
            return self.unsupported_aws_operation("autoscale")
        if choice == 4:
            counts = self.prompt_worker_counts(include_http_requester=True)
            return self.apply_ecs_worker_scale(counts)
        if choice == 5:
            return self.ecs_status()
        return 0

    def monitor_menu(self) -> int:
        choice = self.ui.choose(
            "Monitoring",
            [
                "Compose status for all components",
                "Worker replica counts",
                "Health checks for Command Center endpoints",
                "Recent logs",
                "Follow logs",
                "Error-focused logs with context",
                "Docker stats",
                "Queue and worker heartbeat diagnostics",
                "ECS / Command Center status",
                "Back",
            ],
        )
        if choice == 0:
            return self.compose_action(["status"])
        if choice == 1:
            self.show_worker_counts()
            return 0
        if choice == 2:
            return self.health_checks()
        if choice == 3:
            services = self.select_services(include_infra=True, allow_empty=True, default="none")
            tail = self.ui.prompt_int("Log tail lines", 200, minimum=1)
            return self.compose_action(["logs", "--tail", str(tail), *services])
        if choice == 4:
            services = self.select_services(include_infra=True, allow_empty=True, default="none")
            return self.compose_action(["logs", "--follow", *services])
        if choice == 5:
            services = self.select_services(include_infra=True, allow_empty=True, default="none")
            tail = self.ui.prompt_int("Log tail lines to scan", 400, minimum=1)
            return self.error_logs(services, tail=tail)
        if choice == 6:
            return self.runner.run(self.compose("stats"))
        if choice == 7:
            return self.queue_diagnostics()
        if choice == 8:
            return self.ecs_status()
        return 0

    def operations_menu(self) -> int:
        choice = self.ui.choose(
            "Service operations",
            [
                "Restart selected services",
                "Restart all services",
                "Stop stack",
                "Run smoke test",
                "Clean stack and remove volumes",
                "Show useful URLs",
                "Back",
            ],
        )
        if choice == 0:
            services = self.select_services(include_infra=True, default="changed")
            return self.compose_action(["restart", *services])
        if choice == 1:
            return self.compose_action(["restart"])
        if choice == 2:
            if self.ui.confirm("Stop the local stack?", default=False, assume_yes=self.options.assume_yes):
                return self.compose_action(["down"])
            return 0
        if choice == 3:
            return self.compose_action(["smoke"])
        if choice == 4:
            return self.clean()
        if choice == 5:
            self.show_urls()
            return 0
        return 0

    def ecs_menu(self) -> int:
        choice = self.ui.choose(
            "AWS ECS / ECR",
            [
                "EC2 hybrid deploy: local core + ECS workers",
                "Create ECR repositories",
                "Build and push ECR images for selected services",
                "Deploy/update selected ECS services",
                "Build, push, and deploy selected ECS services",
                "Replace selected ECS worker tasks",
                "Run ECS autoscaler once",
                "Show ECS / Command Center status",
                "Back",
            ],
        )
        if choice == 0:
            return self.compose_action(["--ecs-workers"])
        if choice == 1:
            return self.aws_ecr_ensure_repos([])
        if choice in {2, 3, 4, 5}:
            services = self.select_services(include_infra=False, only_cloudish=True, default="workers")
            if not services:
                self.ui.warn("No services selected.")
                return 0
            if choice == 2:
                return self.aws_ecr_build_push(services)
            if choice == 3:
                return self.unsupported_aws_operation("deploy", services)
            if choice == 4:
                create = self.aws_ecr_ensure_repos(services)
                if create != 0:
                    return create
                build = self.aws_ecr_build_push(services)
                if build != 0:
                    return build
                return self.unsupported_aws_operation("deploy", services)
            return self.unsupported_aws_operation("replace", services)
        if choice == 6:
            return self.unsupported_aws_operation("autoscale")
        if choice == 7:
            return self.ecs_status()
        return 0

    def gcp_menu(self) -> int:
        choice = self.ui.choose(
            "Google Cloud Run",
            [
                "Provision GCP APIs and Artifact Registry",
                "Build and push worker images",
                "Deploy/update worker services (default min=2, max=2)",
                "Build, push, and deploy workers",
                "Scale workers with autoscaling ranges",
                "Set explicit manual worker counts",
                "Show worker status",
                "Teardown worker services",
                "Back",
            ],
        )
        if choice == 0:
            return self.gcp_from_args(["provision"])
        if choice == 1:
            workers = self.select_services(include_infra=False, default="workers")
            return self.gcp_from_args(["build", *workers])
        if choice == 2:
            workers = self.select_services(include_infra=False, default="workers")
            return self.gcp_from_args(["deploy", *workers])
        if choice == 3:
            workers = self.select_services(include_infra=False, default="workers")
            return self.gcp_from_args(["release", *workers])
        if choice == 4:
            workers = [w for w in self.worker_services() if w in GCP_WORKER_SERVICES] or list(GCP_WORKER_SERVICES)
            specs: list[str] = []
            for worker in workers:
                min_count = self.ui.prompt_int(f"{worker} min", 2, minimum=0, maximum=100)
                max_count = self.ui.prompt_int(f"{worker} max", max(min_count, 2), minimum=min_count, maximum=100)
                specs.append(f"{worker}={min_count}:{max_count}")
            return self.gcp_from_args(["scale", *specs])
        if choice == 5:
            workers = [w for w in self.worker_services() if w in GCP_WORKER_SERVICES] or list(GCP_WORKER_SERVICES)
            specs = []
            for worker in workers:
                desired = self.ui.prompt_int(f"{worker} count", 2, minimum=0, maximum=100)
                specs.append(f"{worker}={desired}")
            return self.gcp_from_args(["scale", *specs])
        if choice == 6:
            return self.gcp_from_args(["status"])
        if choice == 7:
            workers = self.select_services(include_infra=False, default="workers")
            return self.gcp_from_args(["teardown", *workers])
        return 0

    # ---------- direct commands ----------

    def deploy_from_args(self, args: Sequence[str]) -> int:
        args = list(args)
        scale_counts, remaining = self.extract_scale_args(args)

        if "--ecs-workers" in remaining:
            code = self.compose_action(["--ecs-workers", *[a for a in remaining if a != "--ecs-workers"]])
        elif "--gcp-workers" in remaining or "--google-workers" in remaining:
            args = [a for a in remaining if a not in {"--gcp-workers", "--google-workers"}]
            code = self.gcp_from_args(["release", *args])
        elif "--fresh" in remaining or "-fresh" in remaining:
            code = self.compose_action(["--fresh", *[a for a in remaining if a not in {"--fresh", "-fresh"}]])
        elif "--image" in remaining or "-image" in remaining:
            services = [a for a in remaining if not a.startswith("-")]
            if services:
                code = self.runner.run(self.compose("build", *services))
                if code == 0:
                    code = self.runner.run(self.compose("up", "-d", "--no-deps", *services))
            else:
                code = self.compose_action(["--image"])
        else:
            services = [a for a in remaining if not a.startswith("-")]
            code = self.compose_action(["--hot", *services])

        if code == 0 and scale_counts:
            return self.apply_local_worker_scale(scale_counts)
        return code

    def scale_from_args(self, args: Sequence[str]) -> int:
        args = list(args)
        if not args:
            return self.scale_menu()

        target = args[0].lower()
        if target in {"local", "compose"}:
            counts = self.parse_count_pairs(args[1:])
            return self.apply_local_worker_scale(counts)
        if target in {"ecs", "aws"}:
            counts = self.parse_count_pairs(args[1:])
            return self.apply_ecs_worker_scale(counts)
        if target in {"gcp", "google"}:
            return self.gcp_from_args(["scale", *args[1:]])
        if target in {"autoscale", "auto"}:
            return self.unsupported_aws_operation("autoscale", args[1:])

        # Default to local for convenience: deploy.py scale worker-spider=3
        counts = self.parse_count_pairs(args)
        return self.apply_local_worker_scale(counts)

    def monitor_from_args(self, args: Sequence[str]) -> int:
        command = args[0]
        rest = list(args[1:])
        if command in {"status", "ps"}:
            return self.compose_action(["status", *rest])
        if not rest:
            return self.monitor_menu()

        topic = rest[0].lower()
        if topic in {"health", "check"}:
            return self.health_checks()
        if topic in {"workers", "scale"}:
            self.show_worker_counts()
            return 0
        if topic in {"queues", "queue", "heartbeat", "heartbeats"}:
            return self.queue_diagnostics()
        if topic in {"ecs", "aws"}:
            return self.ecs_status()
        return self.monitor_menu()

    def logs_from_args(self, args: Sequence[str]) -> int:
        if "--errors" in args:
            remaining = [a for a in args if a != "--errors"]
            return self.error_logs(remaining, tail=400)
        return self.compose_action(["logs", *args])

    def ecs_from_args(self, args: Sequence[str]) -> int:
        if not args:
            return self.ecs_menu()
        action = args[0].lower()
        rest = list(args[1:])

        if action in {"hybrid", "workers"}:
            return self.compose_action(["--ecs-workers", *rest])
        if action == "repos":
            return self.aws_ecr_ensure_repos(rest)
        if action in {"build", "push"}:
            return self.aws_ecr_build_push(rest)
        if action == "deploy":
            return self.unsupported_aws_operation("deploy", rest)
        if action == "release":
            create = self.aws_ecr_ensure_repos(rest)
            if create != 0:
                return create
            build = self.aws_ecr_build_push(rest)
            if build != 0:
                return build
            return self.unsupported_aws_operation("deploy", rest)
        if action == "replace":
            return self.unsupported_aws_operation("replace", rest)
        if action == "autoscale":
            return self.unsupported_aws_operation("autoscale", rest)
        if action == "scale":
            return self.apply_ecs_worker_scale(self.parse_count_pairs(rest))
        if action == "status":
            return self.ecs_status()

        self.ui.error(f"Unknown ECS action: {action}")
        return 2

    def gcp_from_args(self, args: Sequence[str]) -> int:
        args = list(args)
        if not args:
            return self.gcp_menu()

        action = args[0].lower()
        rest = list(args[1:])

        if action in {"help", "-h", "--help"}:
            self.print_gcp_help()
            return 0
        if action in {"init", "configure"}:
            return self.gcp_ensure_config()
        if action in {"provision", "bootstrap"}:
            ensure = self.gcp_ensure_config()
            if ensure != 0:
                return ensure
            return self.gcp_provision()
        if action in {"build", "push"}:
            return self.gcp_build(rest)
        if action == "deploy":
            return self.gcp_deploy(rest)
        if action == "release":
            code = self.gcp_provision()
            if code != 0:
                return code
            code = self.gcp_build(rest)
            if code != 0:
                return code
            return self.gcp_deploy(rest)
        if action == "scale":
            return self.gcp_scale(rest)
        if action == "status":
            return self.gcp_status(rest)
        if action in {"teardown", "destroy", "delete"}:
            return self.gcp_teardown(rest)

        self.ui.error(f"Unknown GCP action: {action}")
        self.print_gcp_help()
        return 2

    def gcp_ensure_config(self) -> int:
        gcp_dir = self.paths.deploy_dir / "gcp"
        gcp_dir.mkdir(parents=True, exist_ok=True)

        env_file = gcp_dir / ".env"
        env_example = gcp_dir / ".env.example"
        if not env_file.exists() and env_example.exists():
            if self.options.dry_run:
                self.ui.info(f"Would create {self.paths.rel(env_file)} from {self.paths.rel(env_example)}")
            else:
                shutil.copyfile(env_example, env_file)
                self.ui.ok(f"Created {self.paths.rel(env_file)}")

        service_env = Path(self.get_env("SERVICE_ENV_FILE", "deployment/gcp/service-env"))
        if not service_env.is_absolute():
            service_env = self.paths.repo_root / service_env
        service_example = gcp_dir / "service-env.example"
        if not service_env.exists() and service_example.exists():
            service_env.parent.mkdir(parents=True, exist_ok=True)
            if self.options.dry_run:
                self.ui.info(f"Would create {self.paths.rel(service_env)} from {self.paths.rel(service_example)}")
            else:
                shutil.copyfile(service_example, service_env)
                self.ui.ok(f"Created {self.paths.rel(service_env)}")

        issues = self.gcp_missing_config()
        if issues:
            self.ui.warn("GCP configuration is incomplete:")
            for issue in issues:
                self.ui.warn(f"  - {issue}")
            self.ui.info(f"Update {self.paths.rel(env_file)} and rerun.")
            return 2
        return 0

    def gcp_missing_config(self) -> list[str]:
        required = ["GCP_PROJECT_ID", "GCP_REGION", "GCP_ARTIFACT_REPOSITORY", "GCP_IMAGE_PREFIX"]
        issues: list[str] = []
        for key in required:
            value = self.get_env(key, "").strip()
            if not value or "replace" in value.lower():
                issues.append(f"{key} is missing or still a placeholder")
        service_env = Path(self.get_env("SERVICE_ENV_FILE", "deployment/gcp/service-env"))
        if not service_env.is_absolute():
            service_env = self.paths.repo_root / service_env
        if not service_env.exists():
            issues.append(f"SERVICE_ENV_FILE does not exist: {self.paths.rel(service_env)}")
        return issues

    def gcp_selected_workers(self, args: Sequence[str]) -> list[str]:
        if not args:
            return list(GCP_WORKER_SERVICES)
        selected: list[str] = []
        for raw in args:
            worker = self.normalize_worker_name(raw)
            if worker not in GCP_WORKER_SERVICES:
                raise SystemExit(f"{raw} is not supported for GCP worker deployment.")
            if worker not in selected:
                selected.append(worker)
        return selected

    def gcp_tag(self) -> str:
        tag = self.get_env("IMAGE_TAG", "").strip()
        if tag and tag.lower() != "latest":
            return tag
        code, sha, _ = self.runner.capture(["git", "rev-parse", "--short=12", "HEAD"])
        if code == 0 and sha.strip():
            return sha.strip()
        return time.strftime("%Y%m%d%H%M%S", time.gmtime())

    def gcp_registry(self) -> str:
        region = self.get_env("GCP_REGION", "us-central1")
        project = self.get_env("GCP_PROJECT_ID")
        repo = self.get_env("GCP_ARTIFACT_REPOSITORY", "argus-engine")
        return f"{region}-docker.pkg.dev/{project}/{repo}"

    def gcp_image_uri(self, worker: str, tag: str) -> str:
        prefix = self.get_env("GCP_IMAGE_PREFIX", "argus-engine").strip("/")
        return f"{self.gcp_registry()}/{prefix}/{worker}:{tag}"

    def gcp_require_tools(self, *, for_build: bool = False) -> int:
        missing: list[str] = []
        for cmd in ["gcloud"]:
            if which(cmd) is None:
                missing.append(cmd)
        if for_build and which("docker") is None:
            missing.append("docker")
        if missing:
            self.ui.error(f"Missing required commands: {', '.join(missing)}")
            return 127
        return 0

    def gcp_login_and_project(self) -> int:
        project = self.get_env("GCP_PROJECT_ID")
        code = self.runner.run(["gcloud", "auth", "list", "--filter=status:ACTIVE", "--format=value(account)"])
        if code != 0:
            return code
        return self.runner.run(["gcloud", "config", "set", "project", project])

    def gcp_provision(self) -> int:
        ensure = self.gcp_ensure_config()
        if ensure != 0:
            return ensure
        req = self.gcp_require_tools(for_build=False)
        if req != 0:
            return req
        login = self.gcp_login_and_project()
        if login != 0:
            return login

        project = self.get_env("GCP_PROJECT_ID")
        region = self.get_env("GCP_REGION", "us-central1")
        repo = self.get_env("GCP_ARTIFACT_REPOSITORY", "argus-engine")

        code = self.runner.run(
            [
                "gcloud",
                "services",
                "enable",
                "run.googleapis.com",
                "artifactregistry.googleapis.com",
                "iam.googleapis.com",
                "--project",
                project,
            ]
        )
        if code != 0:
            return code

        describe = self.runner.run(
            [
                "gcloud",
                "artifacts",
                "repositories",
                "describe",
                repo,
                "--project",
                project,
                "--location",
                region,
            ]
        )
        if describe != 0:
            code = self.runner.run(
                [
                    "gcloud",
                    "artifacts",
                    "repositories",
                    "create",
                    repo,
                    "--project",
                    project,
                    "--location",
                    region,
                    "--repository-format",
                    "docker",
                    "--description",
                    "Argus Engine worker images",
                ]
            )
            if code != 0:
                return code

        return self.runner.run(["gcloud", "auth", "configure-docker", f"{region}-docker.pkg.dev", "--quiet"])

    def gcp_build_base_images(self) -> int:
        code = self.runner.run(
            [
                "docker",
                "build",
                "-t",
                "argus-engine-base:local",
                "-f",
                str(self.paths.deploy_dir / "Dockerfile.base-runtime"),
                str(self.paths.deploy_dir),
            ]
        )
        if code != 0:
            return code
        return self.runner.run(
            [
                "docker",
                "build",
                "-t",
                "argus-recon-base:local",
                "-f",
                str(self.paths.deploy_dir / "Dockerfile.base-recon"),
                str(self.paths.deploy_dir),
            ]
        )

    def gcp_build(self, worker_args: Sequence[str]) -> int:
        ensure = self.gcp_ensure_config()
        if ensure != 0:
            return ensure
        req = self.gcp_require_tools(for_build=True)
        if req != 0:
            return req
        login = self.gcp_login_and_project()
        if login != 0:
            return login
        workers = self.gcp_selected_workers(worker_args)
        tag = self.gcp_tag()

        code = self.runner.run(["gcloud", "auth", "configure-docker", f"{self.get_env('GCP_REGION', 'us-central1')}-docker.pkg.dev", "--quiet"])
        if code != 0:
            return code

        code = self.gcp_build_base_images()
        if code != 0:
            return code

        for worker in workers:
            dockerfile = self.paths.deploy_dir / ("Dockerfile.worker-enum" if worker == "worker-enum" else "Dockerfile.worker")
            image = self.gcp_image_uri(worker, tag)
            args = [
                "docker",
                "build",
                "-f",
                str(dockerfile),
                "--build-arg",
                f"PROJECT_DIR={GCP_SERVICE_PROJECT_DIR[worker]}",
                "--build-arg",
                f"APP_DLL={GCP_SERVICE_APP_DLL[worker]}",
                "--build-arg",
                f"BUILD_SOURCE_STAMP={tag}",
                "--build-arg",
                f"COMPONENT_VERSION={self.version() or '2.6.2'}",
                "-t",
                image,
                str(self.paths.repo_root),
            ]
            if worker == "worker-enum":
                args.extend(
                    [
                        "--build-arg",
                        f"SUBFINDER_VERSION={self.get_env('SUBFINDER_VERSION', '2.14.0')}",
                        "--build-arg",
                        f"AMASS_VERSION={self.get_env('AMASS_VERSION', '5.1.1')}",
                    ]
                )
            code = self.runner.run(args)
            if code != 0:
                return code
            code = self.runner.run(["docker", "push", image])
            if code != 0:
                return code
            self.ui.ok(f"Pushed {image}")
        return 0

    def gcp_worker_slug(self, worker: str) -> str:
        return worker.removeprefix("worker-")

    def gcp_service_name(self, worker: str) -> str:
        return f"argus-worker-{self.gcp_worker_slug(worker)}"

    def gcp_service_default_min(self, worker: str) -> int:
        override = self.get_env(f"GCP_MIN_INSTANCES_{worker.upper().replace('-', '_')}", "")
        if override:
            try:
                return max(0, int(override))
            except ValueError:
                pass
        return 2

    def gcp_service_default_max(self, worker: str) -> int:
        override = self.get_env(f"GCP_MAX_INSTANCES_{worker.upper().replace('-', '_')}", "")
        if override:
            try:
                return max(self.gcp_service_default_min(worker), int(override))
            except ValueError:
                pass
        return max(2, self.gcp_service_default_min(worker))

    def gcp_service_cpu(self, worker: str) -> str:
        return self.get_env(f"GCP_CPU_{worker.upper().replace('-', '_')}", self.get_env("GCP_CPU", "1"))

    def gcp_service_memory(self, worker: str) -> str:
        return self.get_env(f"GCP_MEMORY_{worker.upper().replace('-', '_')}", self.get_env("GCP_MEMORY", "1Gi"))

    def gcp_service_env_file(self) -> Path:
        env_file = Path(self.get_env("SERVICE_ENV_FILE", "deployment/gcp/service-env"))
        if not env_file.is_absolute():
            env_file = self.paths.repo_root / env_file
        return env_file

    def gcp_create_env_vars_file(self) -> Path:
        source = self.gcp_service_env_file()
        values = parse_env_file(source)
        values.setdefault("Argus__SkipStartupDatabase", "true")
        values.setdefault("ARGUS_SKIP_STARTUP_DATABASE", "1")

        temp = tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".yaml", delete=False)
        with temp as out:
            for key, value in values.items():
                out.write(f"{key}: {json.dumps(value)}\n")
        return Path(temp.name)

    def gcp_deploy(self, worker_args: Sequence[str]) -> int:
        ensure = self.gcp_ensure_config()
        if ensure != 0:
            return ensure
        req = self.gcp_require_tools(for_build=False)
        if req != 0:
            return req
        login = self.gcp_login_and_project()
        if login != 0:
            return login

        workers = self.gcp_selected_workers(worker_args)
        tag = self.gcp_tag()
        env_file = self.gcp_create_env_vars_file()
        try:
            for worker in workers:
                image = self.gcp_image_uri(worker, tag)
                service = self.gcp_service_name(worker)
                min_instances = self.gcp_service_default_min(worker)
                max_instances = self.gcp_service_default_max(worker)
                args = [
                    "gcloud",
                    "run",
                    "deploy",
                    service,
                    "--project",
                    self.get_env("GCP_PROJECT_ID"),
                    "--region",
                    self.get_env("GCP_REGION", "us-central1"),
                    "--image",
                    image,
                    "--min-instances",
                    str(min_instances),
                    "--max-instances",
                    str(max_instances),
                    "--cpu",
                    self.gcp_service_cpu(worker),
                    "--memory",
                    self.gcp_service_memory(worker),
                    "--concurrency",
                    self.get_env("GCP_WORKER_CONCURRENCY", "4"),
                    "--env-vars-file",
                    str(env_file),
                    "--ingress",
                    "internal-and-cloud-load-balancing",
                    "--quiet",
                ]
                service_account = self.get_env("GCP_SERVICE_ACCOUNT", "").strip()
                if service_account:
                    args.extend(["--service-account", service_account])
                vpc_connector = self.get_env("GCP_VPC_CONNECTOR", "").strip()
                if vpc_connector:
                    args.extend(["--vpc-connector", vpc_connector])
                vpc_egress = self.get_env("GCP_VPC_EGRESS", "").strip()
                if vpc_egress:
                    args.extend(["--vpc-egress", vpc_egress])
                code = self.runner.run(args)
                if code != 0:
                    return code
                self.ui.ok(f"Deployed {service} min={min_instances} max={max_instances}")
        finally:
            if env_file.exists() and not self.options.dry_run:
                env_file.unlink(missing_ok=True)
        return 0

    def parse_gcp_scale_specs(self, tokens: Sequence[str]) -> dict[str, tuple[int, int]]:
        specs: dict[str, tuple[int, int]] = {}
        for token in tokens:
            if "=" not in token:
                self.ui.warn(f"Ignoring invalid scale token: {token}")
                continue
            raw_worker, raw_value = token.split("=", 1)
            worker = self.normalize_worker_name(raw_worker)
            if worker not in GCP_WORKER_SERVICES:
                raise SystemExit(f"{raw_worker} is not a GCP worker service.")
            if ":" in raw_value:
                raw_min, raw_max = raw_value.split(":", 1)
                min_instances = self.parse_count(raw_min)
                max_instances = max(min_instances, self.parse_count(raw_max))
            else:
                explicit = self.parse_count(raw_value)
                min_instances = explicit
                max_instances = explicit
            specs[worker] = (min_instances, max_instances)
        return specs

    def gcp_scale(self, tokens: Sequence[str]) -> int:
        ensure = self.gcp_ensure_config()
        if ensure != 0:
            return ensure
        req = self.gcp_require_tools(for_build=False)
        if req != 0:
            return req
        login = self.gcp_login_and_project()
        if login != 0:
            return login

        specs = self.parse_gcp_scale_specs(tokens)
        if not specs:
            for worker in GCP_WORKER_SERVICES:
                specs[worker] = (2, 2)

        for worker, (min_instances, max_instances) in specs.items():
            code = self.runner.run(
                [
                    "gcloud",
                    "run",
                    "services",
                    "update",
                    self.gcp_service_name(worker),
                    "--project",
                    self.get_env("GCP_PROJECT_ID"),
                    "--region",
                    self.get_env("GCP_REGION", "us-central1"),
                    "--min-instances",
                    str(min_instances),
                    "--max-instances",
                    str(max_instances),
                    "--quiet",
                ]
            )
            if code != 0:
                return code
            self.ui.ok(f"Scaled {worker} min={min_instances} max={max_instances}")
        return 0

    def gcp_status(self, worker_args: Sequence[str]) -> int:
        ensure = self.gcp_ensure_config()
        if ensure != 0:
            return ensure
        req = self.gcp_require_tools(for_build=False)
        if req != 0:
            return req
        login = self.gcp_login_and_project()
        if login != 0:
            return login

        workers = self.gcp_selected_workers(worker_args)
        exit_code = 0
        for worker in workers:
            code = self.runner.run(
                [
                    "gcloud",
                    "run",
                    "services",
                    "describe",
                    self.gcp_service_name(worker),
                    "--project",
                    self.get_env("GCP_PROJECT_ID"),
                    "--region",
                    self.get_env("GCP_REGION", "us-central1"),
                    "--format",
                    "yaml(metadata.name,status.url,spec.template.metadata.annotations,spec.template.spec.containerConcurrency)",
                ]
            )
            if code != 0:
                exit_code = code
        return exit_code

    def gcp_teardown(self, worker_args: Sequence[str]) -> int:
        ensure = self.gcp_ensure_config()
        if ensure != 0:
            return ensure
        req = self.gcp_require_tools(for_build=False)
        if req != 0:
            return req
        login = self.gcp_login_and_project()
        if login != 0:
            return login

        workers = self.gcp_selected_workers(worker_args)
        for worker in workers:
            code = self.runner.run(
                [
                    "gcloud",
                    "run",
                    "services",
                    "delete",
                    self.gcp_service_name(worker),
                    "--project",
                    self.get_env("GCP_PROJECT_ID"),
                    "--region",
                    self.get_env("GCP_REGION", "us-central1"),
                    "--quiet",
                ]
            )
            if code != 0:
                return code
        return 0

    def print_gcp_help(self) -> None:
        print(
            """
GCP commands:
  deploy.py gcp configure
  deploy.py gcp provision
  deploy.py gcp build [worker...]
  deploy.py gcp deploy [worker...]
  deploy.py gcp release [worker...]
  deploy.py gcp scale [worker=min:max|worker=count ...]
  deploy.py gcp status [worker...]
  deploy.py gcp teardown [worker...]
""".strip()
        )

    # ---------- action helpers ----------

    def compose_action(self, args: Sequence[str]) -> int:
        if not args:
            return self.runner.run(self.compose("up", "-d"))

        cmd = args[0]
        rest = list(args[1:])

        if cmd == "--hot":
            services = [a for a in rest if not a.startswith("-")]
            if services:
                return self.runner.run(self.compose("up", "-d", "--no-deps", *services))
            return self.runner.run(self.compose("up", "-d"))

        if cmd in {"--image", "--fresh"}:
            services = [a for a in rest if not a.startswith("-")]
            build_args = ["build"]
            if cmd == "--fresh":
                build_args.append("--no-cache")
            build_args.extend(services)
            code = self.runner.run(self.compose(*build_args))
            if code != 0:
                return code
            up_args = ["up", "-d"]
            if services:
                up_args.extend(["--no-deps", *services])
            if cmd == "--fresh":
                up_args.append("--force-recreate")
            return self.runner.run(self.compose(*up_args))

        if cmd == "--ecs-workers":
            code = self.runner.run(self.compose("up", "-d"))
            if code != 0:
                return code
            return self.runner.run(
                self.compose(
                    "up",
                    "-d",
                    "--no-deps",
                    "--scale",
                    "worker-spider=0",
                    "--scale",
                    "worker-http-requester=0",
                    "--scale",
                    "worker-enum=0",
                    "--scale",
                    "worker-portscan=0",
                    "--scale",
                    "worker-highvalue=0",
                    "--scale",
                    "worker-techid=0",
                    "worker-spider",
                    "worker-http-requester",
                    "worker-enum",
                    "worker-portscan",
                    "worker-highvalue",
                    "worker-techid",
                )
            )

        if cmd in {"status", "ps"}:
            return self.runner.run(self.compose("ps", *rest))
        if cmd == "down":
            return self.runner.run(self.compose("down", "--remove-orphans"))
        if cmd == "restart":
            return self.runner.run(self.compose("restart", *rest))
        if cmd == "smoke":
            return self.health_checks()
        if cmd == "clean":
            return self.clean()
        if cmd == "logs":
            follow = False
            tail = 200
            services: list[str] = []
            i = 0
            while i < len(rest):
                token = rest[i]
                if token in {"-f", "--follow"}:
                    follow = True
                    i += 1
                    continue
                if token == "--tail" and i + 1 < len(rest):
                    try:
                        tail = int(rest[i + 1])
                    except ValueError:
                        tail = 200
                    i += 2
                    continue
                services.append(token)
                i += 1
            return self.logs(services=services, tail=tail, follow=follow)

        self.ui.error(f"Unsupported legacy command passthrough: {' '.join(args)}")
        return 2

    def all_in_one_deploy(self, args: Sequence[str]) -> int:
        flags = {arg for arg in args if arg.startswith("-")}
        requested = [arg for arg in args if not arg.startswith("-")]
        no_fetch = "--no-fetch" in flags
        no_update = "--no-update" in flags
        use_github_artifacts = "--no-github-artifacts" not in flags and self.get_env(
            "ARGUS_DEPLOY_USE_GITHUB_ARTIFACTS", "1"
        ).lower() not in {"0", "false", "no", "off"}
        use_remote_images = "--no-remote-images" not in flags and self.get_env(
            "ARGUS_DEPLOY_USE_REMOTE_IMAGES", "1"
        ).lower() not in {"0", "false", "no", "off"}

        target = self.resolve_github_target(fetch=not no_fetch)
        if target is None:
            return 2
        target_ref, target_sha = target

        if not no_update:
            code = self.ensure_checkout_at_target(target_ref, target_sha)
            if code != 0:
                return code

        services = self.resolve_deploy_service_args(requested) if requested else self.app_services()
        if not services:
            self.ui.warn("No application services were found to deploy.")
            return 0

        plan = self.services_requiring_deploy(services, target_sha)
        stale = [str(item["service"]) for item in plan if item["deploy"]]

        self.ui.section("All-in-1 incremental deploy plan")
        self.ui.info(f"GitHub target: {target_ref} @ {target_sha[:12]}")
        for item in plan:
            service = str(item["service"])
            status = "deploy" if item["deploy"] else "current"
            print(f"{service:<42} {status:<8} {item['reason']}")

        if not stale:
            self.ui.ok(f"All selected services already match {target_sha[:12]}.")
            return 0

        env = self.build_stamp_env(target_sha)
        pulled_services: list[str] = []
        if use_github_artifacts:
            pulled_services = self.sync_github_artifact_images(stale, target_sha, env)
        if use_remote_images:
            remaining = [service for service in stale if service not in pulled_services]
            pulled_services.extend(self.sync_remote_images(remaining, target_sha, env))

        needs_build = [service for service in stale if service not in pulled_services]

        if needs_build:
            self.ui.info("Rebuilding only stale images: " + ", ".join(needs_build))
            code = self.runner.run(self.compose("build", *needs_build), env=env)
            if code != 0:
                return code
        elif pulled_services:
            self.ui.ok("Remote images were reused for all stale services; no local rebuild needed.")

        self.ui.info("Recreating only stale services with refreshed artifacts.")
        return self.runner.run(self.compose("up", "-d", "--no-deps", *stale), env=env)

    def resolve_github_target(self, *, fetch: bool) -> Optional[tuple[str, str]]:
        ref = self.get_env("ARGUS_DEPLOY_REF", "").strip()
        if not ref:
            code, stdout, _ = self.runner.capture(["git", "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"])
            upstream = stdout.strip()
            ref = upstream if code == 0 and upstream else "origin/main"

        if fetch:
            code = self.fetch_github_ref(ref)
            if code != 0:
                self.ui.error(f"Could not fetch GitHub target {ref}.")
                return None

        if self.options.dry_run:
            code, stdout, stderr = self.capture_git_without_mutation(["rev-parse", ref])
            if code != 0:
                code, stdout, stderr = self.capture_git_without_mutation(["rev-parse", "HEAD"])
                ref = "HEAD"
        else:
            code, stdout, stderr = self.runner.capture(["git", "rev-parse", ref])
        target_sha = stdout.strip()
        if code != 0 or not target_sha:
            if stderr.strip():
                self.ui.warn(stderr.strip())
            self.ui.error(f"Could not resolve deployment target {ref}.")
            return None
        return ref, target_sha

    def capture_git_without_mutation(self, args: Sequence[str]) -> tuple[int, str, str]:
        try:
            completed = subprocess.run(
                ["git", *args],
                cwd=self.paths.repo_root,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            return completed.returncode, completed.stdout, completed.stderr
        except FileNotFoundError:
            return 127, "", "Command not found: git"

    def fetch_github_ref(self, ref: str) -> int:
        if ref.startswith("refs/") or ref == "HEAD" or "/" not in ref:
            return self.runner.run(["git", "fetch", "--prune", "--all"])
        remote, branch = ref.split("/", 1)
        remote_ref = f"refs/remotes/{remote}/{branch}"
        return self.runner.run(["git", "fetch", "--prune", remote, f"+refs/heads/{branch}:{remote_ref}"])

    def ensure_checkout_at_target(self, target_ref: str, target_sha: str) -> int:
        if self.options.dry_run:
            self.ui.info(f"Would ensure the local checkout is fast-forwarded to {target_ref} @ {target_sha[:12]}.")
            return 0

        code, stdout, _ = self.runner.capture(["git", "rev-parse", "HEAD"])
        local_sha = stdout.strip()
        if code != 0 or not local_sha:
            self.ui.error("Could not read the local Git revision.")
            return 2
        if local_sha == target_sha:
            return 0

        code, stdout, _ = self.runner.capture(["git", "status", "--porcelain"])
        if code != 0:
            self.ui.error("Could not inspect the Git worktree before deployment.")
            return code
        if stdout.strip():
            self.ui.error(
                "GitHub is ahead of this checkout, but the worktree has uncommitted changes. "
                "Commit or stash them before running all-in-1 deploy."
            )
            return 2

        self.ui.info(f"Fast-forwarding local checkout from {local_sha[:12]} to {target_sha[:12]}.")
        return self.runner.run(["git", "merge", "--ff-only", target_ref])

    def resolve_deploy_service_args(self, args: Sequence[str]) -> list[str]:
        available = self.app_services()
        by_name = {service.lower(): service for service in available}
        selected: list[str] = []

        for raw in args:
            key = raw.strip().lower().replace("_", "-")
            if not key:
                continue
            if key in {"all", "app", "apps", "projects"}:
                for service in available:
                    if service not in selected:
                        selected.append(service)
                continue
            if key in {"worker", "workers"}:
                for service in self.worker_services():
                    if service not in selected:
                        selected.append(service)
                continue
            match = by_name.get(key)
            if match is None and not key.startswith("worker-"):
                match = by_name.get(f"worker-{key}")
            if match is None:
                self.ui.warn(f"Ignoring unknown deploy service: {raw}")
                continue
            if match not in selected:
                selected.append(match)

        return selected

    def services_requiring_deploy(self, services: Sequence[str], target_sha: str) -> list[dict[str, object]]:
        plan: list[dict[str, object]] = []
        for service in services:
            revisions = self.deployed_revisions(service)
            if not revisions:
                plan.append({"service": service, "deploy": True, "reason": "not running or no deployed stamp found"})
                continue

            target_sig = self.service_source_signature(service, target_sha)
            if target_sig is None:
                joined = ", ".join(revision[:12] for revision in revisions)
                plan.append(
                    {
                        "service": service,
                        "deploy": True,
                        "reason": f"could not resolve service source fingerprint; deployed {joined}",
                    }
                )
                continue

            deployed_sigs: list[dict[str, str]] = []
            unresolved_revisions: list[str] = []
            for revision in revisions:
                sig = self.service_source_signature(service, revision)
                if sig is None:
                    unresolved_revisions.append(revision)
                    continue
                deployed_sigs.append(sig)

            if unresolved_revisions:
                joined = ", ".join(revision[:12] for revision in unresolved_revisions)
                plan.append(
                    {
                        "service": service,
                        "deploy": True,
                        "reason": f"could not inspect deployed revision(s): {joined}",
                    }
                )
                continue

            mismatch = any(sig["commit"] != target_sig["commit"] for sig in deployed_sigs)
            if mismatch:
                deployed_view = ", ".join(
                    f"{sig['commit'][:12]} @ {sig['date_human']}" for sig in deployed_sigs
                )
                plan.append(
                    {
                        "service": service,
                        "deploy": True,
                        "reason": (
                            f"service updated on GitHub at {target_sig['date_human']} "
                            f"({target_sig['commit'][:12]}); deployed fingerprint(s): {deployed_view}"
                        ),
                    }
                )
                continue

            plan.append(
                {
                    "service": service,
                    "deploy": False,
                    "reason": (
                        f"source unchanged since {target_sig['date_human']} "
                        f"({target_sig['commit'][:12]})"
                    ),
                }
            )
        return plan

    def service_source_signature(self, service: str, git_ref: str) -> Optional[dict[str, str]]:
        key = (service, git_ref)
        cached = self._service_signature_cache.get(key)
        if cached is not None:
            return cached

        source_paths = self.service_source_paths(service)
        if not source_paths:
            return None

        code, stdout, _ = self.runner.capture(
            ["git", "log", "-1", "--format=%H|%ct|%cI", git_ref, "--", *source_paths]
        )
        if code != 0 or not stdout.strip():
            return None

        parts = stdout.strip().split("|", 2)
        if len(parts) != 3:
            return None

        commit, epoch_raw, iso = (part.strip() for part in parts)
        try:
            epoch = int(epoch_raw)
        except ValueError:
            epoch = 0
        date_human = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime(epoch)) if epoch > 0 else iso

        signature = {
            "commit": commit,
            "epoch": str(epoch),
            "iso": iso,
            "date_human": date_human,
        }
        self._service_signature_cache[key] = signature
        return signature

    def service_source_paths(self, service: str) -> list[str]:
        cached = self._service_source_paths_cache.get(service)
        if cached is not None:
            return list(cached)

        row = self.service_catalog().get(service)
        if row is None:
            base = PROJECT_HINTS.get(service, [])
            normalized = sorted({path.rstrip("/") for path in base})
            self._service_source_paths_cache[service] = normalized
            return list(normalized)

        source_dirs = set(self.project_reference_closure(str(row["csproj"])))
        source_dirs.update(path.rstrip("/") for path in row.get("extra_source_dirs", []))
        source_dirs.update(path.rstrip("/") for path in PROJECT_HINTS.get(service, []))

        build_inputs = set(path.rstrip("/") for path in GLOBAL_BUILD_INPUTS)
        build_inputs.add(str(row.get("dockerfile", "")).rstrip("/"))

        dockerfile = str(row.get("dockerfile", "")).rstrip("/")
        for hint_path, hint_services in DOCKERFILE_RESOURCE_HINTS.items():
            if hint_services == "all" or service in hint_services:
                build_inputs.add(hint_path.rstrip("/"))

        if dockerfile == "deployment/Dockerfile.worker-enum":
            build_inputs.add("deployment/Dockerfile.base-recon")
            build_inputs.add("deployment/wordlists")
            build_inputs.add("deployment/artifacts/recon-tools")

        combined = sorted(item for item in {*source_dirs, *build_inputs} if item)
        self._service_source_paths_cache[service] = combined
        return list(combined)

    def project_reference_closure(self, csproj_relpath: str) -> set[str]:
        start = (self.paths.repo_root / csproj_relpath).resolve()
        visited: set[Path] = set()
        dirs: set[str] = set()

        def visit(csproj: Path) -> None:
            if csproj in visited:
                return
            visited.add(csproj)
            if not csproj.exists():
                return

            try:
                rel_dir = csproj.parent.resolve().relative_to(self.paths.repo_root.resolve())
                dirs.add(str(rel_dir).replace("\\", "/").rstrip("/"))
            except ValueError:
                pass

            for ref in self.project_references(csproj):
                visit(ref)

        visit(start)
        return dirs

    def project_references(self, csproj: Path) -> list[Path]:
        try:
            tree = ET.parse(csproj)
        except (ET.ParseError, OSError):
            return []

        refs: list[Path] = []
        for item in tree.iter():
            if item.tag.split("}")[-1] != "ProjectReference":
                continue
            include = item.attrib.get("Include")
            if not include:
                continue
            refs.append((csproj.parent / include).resolve())
        return refs

    def deployed_revisions(self, service: str) -> list[str]:
        code, stdout, _ = self.runner.capture(self.compose("ps", "-q", service))
        if code != 0:
            return []

        revisions: list[str] = []
        for container_id in [line.strip() for line in stdout.splitlines() if line.strip()]:
            revision = self.container_revision(container_id)
            if revision and revision not in revisions:
                revisions.append(revision)
        return revisions

    def container_revision(self, container_id: str) -> str:
        code, stdout, _ = self.runner.capture(["docker", "inspect", container_id])
        if code != 0 or not stdout.strip():
            return ""
        try:
            data = json.loads(stdout)[0]
        except (IndexError, json.JSONDecodeError, TypeError):
            return ""

        config = data.get("Config") or {}
        labels = config.get("Labels") or {}
        revision = str(labels.get("org.opencontainers.image.revision") or "").strip()
        if revision:
            return revision

        env = self.env_list_to_dict(config.get("Env") or [])
        revision = env.get("ARGUS_BUILD_STAMP", "").strip() or env.get("BUILD_SOURCE_STAMP", "").strip()
        if revision:
            return revision

        image_id = str(data.get("Image") or "").strip()
        if not image_id:
            return ""
        code, image_stdout, _ = self.runner.capture(["docker", "image", "inspect", image_id])
        if code != 0 or not image_stdout.strip():
            return ""
        try:
            image_data = json.loads(image_stdout)[0]
        except (IndexError, json.JSONDecodeError, TypeError):
            return ""
        image_config = image_data.get("Config") or {}
        image_labels = image_config.get("Labels") or {}
        return str(image_labels.get("org.opencontainers.image.revision") or "").strip()

    def env_list_to_dict(self, values: Sequence[str]) -> dict[str, str]:
        env: dict[str, str] = {}
        for value in values:
            if "=" not in value:
                continue
            key, raw = value.split("=", 1)
            env[key] = raw
        return env

    def revision_matches(self, deployed: str, target_sha: str) -> bool:
        deployed = deployed.strip()
        target_sha = target_sha.strip()
        if not deployed or deployed.lower() in {"unknown", "local"}:
            return False
        return deployed == target_sha or deployed.startswith(target_sha) or target_sha.startswith(deployed)

    def build_stamp_env(self, target_sha: str) -> dict[str, str]:
        component_version = self.version() or target_sha[:12]
        build_time = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        return {
            "ARGUS_ENGINE_VERSION": component_version,
            "COMPONENT_VERSION": component_version,
            "BUILD_SOURCE_STAMP": target_sha,
            "BUILD_TIME_UTC": build_time,
            "ARGUS_BUILD_TIME_UTC": build_time,
        }

    def sync_github_artifact_images(self, services: Sequence[str], target_sha: str, env: Mapping[str, str]) -> list[str]:
        if not services:
            return []
        if which("gh") is None or which("docker") is None:
            self.ui.info("GitHub artifact reuse disabled: gh CLI or docker is not available.")
            return []

        repo = self.github_repository_slug()
        if not repo:
            self.ui.info("GitHub artifact reuse disabled: could not infer repository slug.")
            return []

        run_id = self.github_successful_run_for_sha(repo, target_sha)
        if not run_id:
            self.ui.info(f"No successful GitHub Actions run found for {target_sha[:12]}; skipping artifact reuse.")
            return []

        artifacts = self.github_artifacts_for_run(repo, run_id)
        if artifacts is None:
            self.ui.info("Could not list GitHub artifacts for the target run; skipping artifact reuse.")
            return []

        component_version = env.get("ARGUS_ENGINE_VERSION") or self.version() or target_sha[:12]
        pulled: list[str] = []

        with tempfile.TemporaryDirectory(prefix="argus-gh-artifacts-") as tempdir:
            for service in services:
                artifact_name = f"argus-image-{service}-{target_sha}"
                if artifact_name not in artifacts:
                    continue

                self.ui.info(f"Trying GitHub artifact for {service}: {artifact_name}")
                download = self.runner.run(
                    ["gh", "run", "download", str(run_id), "--repo", repo, "--name", artifact_name, "--dir", tempdir]
                )
                if download != 0:
                    self.ui.warn(f"Failed downloading artifact {artifact_name}; falling back.")
                    continue

                archive = Path(tempdir) / f"{service}-{target_sha}.tar.gz"
                if not archive.exists():
                    self.ui.warn(f"Artifact {artifact_name} did not include expected archive {archive.name}.")
                    continue

                load = self.runner.run(["docker", "load", "-i", str(archive)])
                if load != 0:
                    self.ui.warn(f"Failed loading Docker archive for {service}; falling back.")
                    continue

                loaded_ref = f"argus-engine/{service}:ci"
                local_ref = f"argus-engine/{service}:{component_version}"
                tag = self.runner.run(["docker", "tag", loaded_ref, local_ref])
                if tag != 0:
                    self.ui.warn(f"Loaded artifact for {service} but failed to tag {local_ref}; falling back.")
                    continue

                pulled.append(service)
                self.ui.ok(f"Reused GitHub build artifact for {service} without local rebuild.")

        return pulled

    def github_repository_slug(self) -> str:
        explicit = self.get_env("GITHUB_REPOSITORY", "").strip()
        if explicit and "/" in explicit:
            return explicit

        code, stdout, _ = self.runner.capture(["git", "remote", "get-url", "origin"])
        if code != 0:
            return ""
        url = stdout.strip()
        if not url:
            return ""

        if url.startswith("git@github.com:"):
            slug = url.split("git@github.com:", 1)[1]
        elif "github.com/" in url:
            slug = url.split("github.com/", 1)[1]
        else:
            return ""

        if slug.endswith(".git"):
            slug = slug[:-4]
        return slug.strip("/")

    def github_successful_run_for_sha(self, repo: str, sha: str) -> int:
        code, stdout, _ = self.runner.capture(
            ["gh", "api", f"repos/{repo}/actions/runs", "-f", f"head_sha={sha}", "-f", "status=completed", "-f", "per_page=30"]
        )
        if code != 0 or not stdout.strip():
            return 0

        try:
            payload = json.loads(stdout)
        except json.JSONDecodeError:
            return 0

        for run in payload.get("workflow_runs", []):
            if str(run.get("conclusion", "")).lower() == "success":
                try:
                    return int(run.get("id") or 0)
                except (TypeError, ValueError):
                    continue
        return 0

    def github_artifacts_for_run(self, repo: str, run_id: int) -> Optional[set[str]]:
        code, stdout, _ = self.runner.capture(
            ["gh", "api", f"repos/{repo}/actions/runs/{run_id}/artifacts", "-f", "per_page=100"]
        )
        if code != 0 or not stdout.strip():
            return None

        try:
            payload = json.loads(stdout)
        except json.JSONDecodeError:
            return None

        names: set[str] = set()
        for artifact in payload.get("artifacts", []):
            name = str(artifact.get("name") or "").strip()
            expired = bool(artifact.get("expired"))
            if name and not expired:
                names.add(name)
        return names

    def sync_remote_images(self, services: Sequence[str], target_sha: str, env: Mapping[str, str]) -> list[str]:
        if not services:
            return []

        registry = self.try_ecr_registry()
        if not registry:
            self.ui.info("Remote image reuse disabled: AWS/ECR configuration not available.")
            return []

        region = self.get_env("AWS_REGION", "us-east-1")
        component_version = env.get("ARGUS_ENGINE_VERSION") or self.version() or target_sha[:12]
        pulled: list[str] = []

        for service in services:
            row = self.service_catalog().get(service)
            if not row or not bool(row.get("ecr_enabled")):
                continue

            remote_repo = f"{registry}/{self.ecr_repository(service)}"
            remote_ref = f"{remote_repo}:{target_sha}"
            local_ref = f"argus-engine/{service}:{component_version}"

            self.ui.info(f"Trying remote artifact for {service}: {remote_ref}")
            pull = self.runner.run(["docker", "pull", remote_ref])
            if pull != 0:
                self.ui.warn(f"Remote artifact unavailable for {service}; will build locally.")
                continue

            tag = self.runner.run(["docker", "tag", remote_ref, local_ref])
            if tag != 0:
                self.ui.warn(f"Pulled {service} but failed to retag {local_ref}; falling back to local build.")
                continue

            pulled.append(service)
            self.ui.ok(f"Reused remote artifact for {service} without local rebuild.")

        return pulled

    def try_ecr_registry(self) -> str:
        account_id = self.get_env("AWS_ACCOUNT_ID", "").strip()
        region = self.get_env("AWS_REGION", "us-east-1").strip()
        if not account_id or not region:
            return ""
        if which("docker") is None:
            return ""
        return f"{account_id}.dkr.ecr.{region}.amazonaws.com"

    def validate_manifests(self, args: Sequence[str]) -> int:
        compose_files = [self.paths.compose_file]
        if any(arg in {"--ci", "ci"} for arg in args):
            ci_file = self.paths.deploy_dir / "docker-compose.ci.yml"
            if ci_file.exists():
                compose_files.append(ci_file)

        command = [*self.compose_cmd()]
        for compose_file in compose_files:
            command.extend(["-f", str(compose_file)])
        command.extend(["config", "--quiet"])
        return self.runner.run(command)

    def service_catalog(self) -> dict[str, dict[str, object]]:
        if self._service_catalog_cache is not None:
            return dict(self._service_catalog_cache)

        catalog_path = self.paths.deploy_dir / "service-catalog.tsv"
        services: dict[str, dict[str, object]] = {}
        if not catalog_path.exists():
            return services

        with catalog_path.open(encoding="utf-8") as handle:
            headers: list[str] = []
            for raw in handle:
                line = raw.strip()
                if not line:
                    continue
                if line.startswith("#"):
                    headers = line.lstrip("#").strip().split()
                    continue
                parts = line.split("\t")
                if not headers or len(parts) < len(headers):
                    continue
                row = dict(zip(headers, parts))
                extras = [item.strip() for item in row.get("extra_source_dirs", "").split(",") if item.strip()]
                service_name = row["service"]
                project_dir = row.get("project_dir", "")
                services[service_name] = {
                    **row,
                    "service": service_name,
                    "project_dir": project_dir,
                    "csproj": f"src/{project_dir}/{project_dir}.csproj" if project_dir else "",
                    "project_path": f"src/{project_dir}" if project_dir else "",
                    "extra_source_dirs": extras,
                    "ecr_enabled": row.get("ecr_enabled") == "1",
                }

        self._service_catalog_cache = services
        return dict(services)

    def selected_ecr_services(self, args: Sequence[str]) -> list[dict[str, object]]:
        catalog = self.service_catalog()
        requested = [arg for arg in args if arg and arg != "all"]
        names = requested or [name for name, row in catalog.items() if bool(row.get("ecr_enabled"))]
        missing = [name for name in names if name not in catalog]
        if missing:
            raise SystemExit(f"Unknown ECR service(s): {', '.join(missing)}")
        return [catalog[name] for name in names if bool(catalog[name].get("ecr_enabled"))]

    def ecr_repository(self, service: str) -> str:
        prefix = self.get_env("ECR_PREFIX", "argus-v2").strip("/")
        return f"{prefix}/{service}" if prefix else service

    def ecr_registry(self) -> str:
        account_id = self.get_env("AWS_ACCOUNT_ID")
        region = self.get_env("AWS_REGION", "us-east-1")
        if not account_id:
            raise SystemExit("AWS_ACCOUNT_ID is required for ECR image publishing.")
        return f"{account_id}.dkr.ecr.{region}.amazonaws.com"

    def git_short_sha(self) -> str:
        code, stdout, _ = self.runner.capture(["git", "rev-parse", "--short=12", "HEAD"])
        if code == 0 and stdout.strip():
            return stdout.strip()
        return "local"

    def aws_ecr_ensure_repos(self, args: Sequence[str]) -> int:
        services = self.selected_ecr_services(args)
        region = self.get_env("AWS_REGION", "us-east-1")
        for row in services:
            repo = self.ecr_repository(row["service"])
            exists = self.runner.capture(["aws", "ecr", "describe-repositories", "--region", region, "--repository-names", repo])
            if exists[0] == 0:
                self.ui.ok(f"ECR repository exists: {repo}")
                continue
            code = self.runner.run(["aws", "ecr", "create-repository", "--region", region, "--repository-name", repo])
            if code != 0:
                return code
        return 0

    def aws_ecr_build_push(self, args: Sequence[str]) -> int:
        services = self.selected_ecr_services(args)
        registry = self.ecr_registry()
        image_tag = self.get_env("IMAGE_TAG") or self.get_env("BUILD_SOURCE_STAMP") or self.git_short_sha()
        source_stamp = self.get_env("BUILD_SOURCE_STAMP", image_tag)
        component_version = self.get_env("COMPONENT_VERSION", self.get_env("ARGUS_ENGINE_VERSION", image_tag))

        for row in services:
            service = row["service"]
            repo = f"{registry}/{self.ecr_repository(service)}"
            code = self.runner.run(
                [
                    "docker",
                    "buildx",
                    "build",
                    "--platform",
                    self.get_env("DOCKER_PLATFORM", "linux/amd64"),
                    "--push",
                    "-f",
                    row["dockerfile"],
                    "-t",
                    f"{repo}:{image_tag}",
                    "-t",
                    f"{repo}:latest",
                    "--build-arg",
                    f"PROJECT_DIR={row['project_dir']}",
                    "--build-arg",
                    f"APP_DLL={row['app_dll']}",
                    "--build-arg",
                    f"BUILD_SOURCE_STAMP={source_stamp}",
                    "--build-arg",
                    f"COMPONENT_VERSION={component_version}",
                    ".",
                ]
            )
            if code != 0:
                return code
        return 0

    def clean(self) -> int:
        self.ui.warn("This removes compose containers, orphans, volumes, and hot-publish output.")
        if not self.ui.confirm("Continue with clean?", default=False, assume_yes=self.options.assume_yes):
            return 0
        code = self.runner.run(self.compose("down", "--remove-orphans", "--volumes"))
        hot_publish = self.paths.deploy_dir / ".hot-publish"
        if hot_publish.exists() and not self.options.dry_run:
            shutil.rmtree(hot_publish, ignore_errors=True)
        return code

    def unsupported_aws_operation(self, name: str, args: Sequence[str] = ()) -> int:
        _ = args
        self.ui.error(
            f"AWS operation '{name}' has not been ported into standalone deploy.py yet."
        )
        return 2

    def health_checks(self) -> int:
        self.ui.section("Health checks")
        failures = 0
        for service, url in HEALTH_ENDPOINTS.items():
            start = time.time()
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "argus-deploy-python/1.0"})
                with urllib.request.urlopen(req, timeout=5) as response:
                    elapsed_ms = int((time.time() - start) * 1000)
                    if 200 <= response.status < 300:
                        self.ui.ok(f"{service:<40} {response.status} {elapsed_ms}ms {url}")
                    else:
                        failures += 1
                        self.ui.warn(f"{service:<40} HTTP {response.status} {url}")
            except urllib.error.HTTPError as exc:
                failures += 1
                self.ui.warn(f"{service:<40} HTTP {exc.code} {url}")
            except Exception as exc:
                failures += 1
                self.ui.warn(f"{service:<40} unavailable: {exc}")
        return 1 if failures else 0

    def error_logs(self, services: Sequence[str], *, tail: int = 400) -> int:
        code, stdout, stderr = self.runner.capture(self.compose("logs", "--tail", str(tail), *services))
        if code != 0:
            if stderr.strip():
                print(stderr.strip())
            return code
        patterns = (" error", "exception", "failed", "fatal", "panic")
        for line in stdout.splitlines():
            lower = line.lower()
            if any(pattern in lower for pattern in patterns):
                print(line)
        return 0

    def logs(self, *, services: Sequence[str], tail: int = 200, follow: bool = False) -> int:
        args = ["logs", "--tail", str(tail)]
        if follow:
            args.append("-f")
        args.extend(services)
        return self.runner.run(self.compose(*args))

    def queue_diagnostics(self) -> int:
        self.ui.section("Queue and worker diagnostics")
        queries = [
            (
                "Worker heartbeats",
                """
SELECT "WorkerKey",
       "HostName",
       "IsHealthy",
       "ActiveConsumerCount",
       "LastHeartbeatUtc",
       now() - "LastHeartbeatUtc" AS heartbeat_age,
       "HealthMessage"
FROM worker_heartbeats
ORDER BY "LastHeartbeatUtc" DESC
LIMIT 50;
""",
            ),
            (
                "HTTP request queue by state",
                """
SELECT state, count(*) AS rows
FROM http_request_queue
GROUP BY state
ORDER BY rows DESC;
""",
            ),
            (
                "Recent bus consumer activity",
                """
SELECT direction,
       consumer_type,
       host_name,
       max(occurred_at_utc) AS last_seen,
       count(*) AS events
FROM bus_journal
WHERE occurred_at_utc >= now() - interval '30 minutes'
GROUP BY direction, consumer_type, host_name
ORDER BY last_seen DESC
LIMIT 50;
""",
            ),
        ]

        exit_code = 0
        for title, sql in queries:
            self.ui.section(title)
            code = self.runner.run(
                self.compose(
                    "exec",
                    "-T",
                    "postgres",
                    "psql",
                    "-v",
                    "ON_ERROR_STOP=1",
                    "-U",
                    "argus",
                    "-d",
                    "argus_engine",
                    "-c",
                    sql,
                )
            )
            if code != 0:
                exit_code = code
        return exit_code

    def ecs_status(self) -> int:
        region = self.get_env("AWS_REGION", "us-east-1")
        cluster = self.get_env("ECS_CLUSTER", "argus-engine")
        service_names = [self.ecs_service_name(worker) for worker in self.worker_services()]
        if not service_names:
            service_names = [str(meta["ecs_default"]) for meta in WORKERS.values()]

        return self.runner.run(
            [
                "aws",
                "ecs",
                "describe-services",
                "--region",
                region,
                "--cluster",
                cluster,
                "--services",
                *service_names,
                "--query",
                "services[].{service:serviceName,status:status,desired:desiredCount,running:runningCount,pending:pendingCount,taskDefinition:taskDefinition}",
                "--output",
                "table",
            ]
        )

    def apply_local_worker_scale(self, counts: Mapping[str, int]) -> int:
        if not counts:
            self.ui.warn("No worker counts provided.")
            return 0

        normalized = self.normalize_worker_counts(counts)
        env_updates = {str(WORKERS[service]["local_env"]): str(count) for service, count in normalized.items()}
        args: list[str] = ["up", "-d"]

        for service, count in normalized.items():
            args.extend(["--scale", f"{service}={count}"])
        args.extend(normalized.keys())

        self.ui.section("Applying local worker scale")
        for service, count in normalized.items():
            self.ui.info(f"{service}: {count} replica(s)")

        code = self.runner.run(self.compose(*args), env=env_updates)
        if code == 0:
            self.show_worker_counts()
        return code

    def apply_ecs_worker_scale(self, counts: Mapping[str, int]) -> int:
        if not counts:
            self.ui.warn("No ECS worker counts provided.")
            return 0

        normalized = self.normalize_worker_counts(counts, include_http_requester=True)
        region = self.get_env("AWS_REGION", "us-east-1")
        cluster = self.get_env("ECS_CLUSTER", "argus-engine")

        self.ui.section("Applying ECS desired worker counts")
        exit_code = 0
        for service, count in normalized.items():
            ecs_service = self.ecs_service_name(service)
            self.ui.info(f"{service} → {ecs_service}: desired={count}")
            code = self.runner.run(
                [
                    "aws",
                    "ecs",
                    "update-service",
                    "--region",
                    region,
                    "--cluster",
                    cluster,
                    "--service",
                    ecs_service,
                    "--desired-count",
                    str(count),
                ]
            )
            if code != 0:
                exit_code = code
        return exit_code

    # ---------- info/display ----------

    def show_context(self, *, compact: bool = False) -> None:
        if compact:
            self.ui.info(f"Repo: {self.paths.repo_root}")
            version = self.version()
            if version:
                self.ui.info(f"Version: {version}")
            if self.options.dry_run:
                self.ui.warn("Dry run: commands will be printed, not executed.")
            return

        self.ui.section("Context")
        print(f"Repository : {self.paths.repo_root}")
        print(f"Deploy dir : {self.paths.deploy_dir}")
        print(f"Compose    : {self.paths.compose_file}")
        version = self.version()
        if version:
            print(f"Version    : {version}")
        print(f"Dry run    : {self.options.dry_run}")

    def version(self) -> str:
        for source in [
            os.environ.get("ARGUS_ENGINE_VERSION"),
            self.env.get("ARGUS_ENGINE_VERSION"),
        ]:
            if source:
                return source
        version_file = self.paths.repo_root / "VERSION"
        if version_file.exists():
            try:
                return version_file.read_text(encoding="utf-8").strip()
            except OSError:
                return ""
        return ""

    def show_urls(self) -> None:
        self.ui.section("Useful URLs")
        urls = [
            ("Command Center gateway", "http://localhost:8081/"),
            ("Command Center web", "http://localhost:8082/"),
            ("Operations API", "http://localhost:8083/health/ready"),
            ("Discovery API", "http://localhost:8084/health/ready"),
            ("Worker Control API", "http://localhost:8085/health/ready"),
            ("Maintenance API", "http://localhost:8086/health/ready"),
            ("Updates API", "http://localhost:8087/health/ready"),
            ("Realtime host", "http://localhost:8088/health/ready"),
            ("RabbitMQ admin", "http://localhost:15672/  user/pass: argus / argus"),
            ("Postgres", "localhost:5432 db=argus_engine user=argus"),
        ]
        for name, url in urls:
            print(f"{name:<28} {url}")

    def show_services(self) -> None:
        self.ui.section("Deployable components")
        for service in self.services():
            kind = "worker" if service in WORKERS else "infra" if service in {"postgres", "redis", "rabbitmq", "filestore-db-init"} else "app"
            print(f"{service:<42} {kind}")

    def show_worker_counts(self) -> None:
        self.ui.section("Local worker replica counts")
        for worker in self.worker_services():
            count = self.local_service_count(worker)
            meta = WORKERS[worker]
            print(f"{worker:<30} {count:>3}  {meta['description']}")

    def show_changed_services(self) -> None:
        changed = self.changed_files()
        affected = self.changed_services(changed)
        self.ui.section("Changed files")
        if changed:
            for file in changed:
                print(file)
        else:
            self.ui.warn("No Git changes detected.")
        self.ui.section("Likely affected components")
        if affected:
            for service in affected:
                print(service)
        else:
            self.ui.warn("No affected components detected.")

    # ---------- selection/prompting ----------

    def select_services(
        self,
        *,
        include_infra: bool,
        allow_empty: bool = False,
        only_cloudish: bool = False,
        default: str = "all",
    ) -> list[str]:
        candidates = self.services() if include_infra else self.app_services()
        if only_cloudish:
            candidates = [service for service in candidates if service not in {"filestore-db-init"}]

        changed = self.changed_services(self.changed_files())
        worker_names = self.worker_services()
        infra_names = {"postgres", "redis", "rabbitmq", "filestore-db-init"}

        self.ui.section("Components")
        for idx, service in enumerate(candidates, 1):
            mark = "*" if service in changed else " "
            kind = "worker" if service in WORKERS else "infra" if service in infra_names else "app"
            print(f"{idx:>2}. {mark} {service:<42} {kind}")

        if changed:
            self.ui.info("* = likely affected by current Git changes.")

        if default == "changed" and not changed:
            default = "all"
        if default == "workers":
            default_value = "workers"
        elif allow_empty:
            default_value = default if default in {"all", "changed", "workers"} else "none"
        else:
            default_value = default if default in {"all", "changed", "workers"} else "all"

        raw = self.ui.prompt(
            "Select numbers/names separated by commas, or all/changed/app/infra/workers/none",
            default_value,
        ).strip()

        if raw.lower() == "none":
            return [] if allow_empty else candidates
        if raw.lower() == "all":
            return candidates
        if raw.lower() == "changed":
            return [s for s in candidates if s in changed] or ([] if allow_empty else candidates)
        if raw.lower() == "workers":
            return [s for s in candidates if s in worker_names]
        if raw.lower() == "app":
            return [s for s in candidates if s not in worker_names and s not in infra_names]
        if raw.lower() == "infra":
            return [s for s in candidates if s in infra_names]

        selected: list[str] = []
        by_name = {s.lower(): s for s in candidates}
        for token in re.split(r"[,\s]+", raw):
            if not token:
                continue
            if token.isdigit():
                idx = int(token)
                if 1 <= idx <= len(candidates):
                    selected.append(candidates[idx - 1])
                else:
                    self.ui.warn(f"Ignoring out-of-range selection: {token}")
                continue
            match = by_name.get(token.lower())
            if match:
                selected.append(match)
            else:
                self.ui.warn(f"Ignoring unknown component: {token}")

        # De-duplicate while preserving order.
        deduped: list[str] = []
        for service in selected:
            if service not in deduped:
                deduped.append(service)
        return deduped

    def select_worker(self) -> str:
        workers = self.worker_services()
        if not workers:
            workers = list(WORKERS)
        idx = self.ui.choose("Select worker", [f"{w} — {WORKERS[w]['description']}" for w in workers])
        return workers[idx]

    def prompt_worker_counts(self, *, include_http_requester: bool = True) -> dict[str, int]:
        counts: dict[str, int] = {}
        self.ui.section("Worker counts")
        self.ui.info("Set a worker to 0 to stop that worker class.")
        for worker in self.worker_services():
            if worker == "worker-http-requester" and not include_http_requester:
                continue
            current = self.local_service_count(worker)
            counts[worker] = self.ui.prompt_int(f"{worker} replicas", current, minimum=0)
        return counts

    # ---------- lower-level helpers ----------

    def local_service_count(self, service: str) -> int:
        code, stdout, _ = self.runner.capture(self.compose("ps", "-q", service))
        if code != 0:
            return 0
        return len([line for line in stdout.splitlines() if line.strip()])

    def changed_files(self) -> list[str]:
        if not (self.paths.repo_root / ".git").exists():
            return []

        files: set[str] = set()

        def collect(args: Sequence[str]) -> None:
            code, stdout, _ = self.runner.capture(["git", *args])
            if code == 0:
                for line in stdout.splitlines():
                    line = line.strip()
                    if line:
                        files.add(line.replace("\\", "/"))

        collect(["diff", "--name-only"])
        collect(["diff", "--cached", "--name-only"])
        collect(["ls-files", "--others", "--exclude-standard"])

        code, upstream, _ = self.runner.capture(["git", "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"])
        upstream = upstream.strip()
        if code == 0 and upstream:
            code, merge_base, _ = self.runner.capture(["git", "merge-base", "HEAD", upstream])
            merge_base = merge_base.strip()
            if code == 0 and merge_base:
                collect(["diff", "--name-only", f"{merge_base}...HEAD"])

        return sorted(files)

    def changed_services(self, files: Sequence[str]) -> list[str]:
        if not files:
            return []

        normalized = [f.replace("\\", "/") for f in files]
        if any(
            file in GLOBAL_INVALIDATORS
            or file == "deploy.py"
            or file == "deploy"
            or file.startswith("deployment/")
            for file in normalized
        ):
            return sorted(self.app_services())

        affected: set[str] = set()
        for service, hints in PROJECT_HINTS.items():
            for file in normalized:
                if any(file.startswith(hint) for hint in hints):
                    affected.add(service)
        return sorted(service for service in affected if service in self.services())

    def extract_scale_args(self, args: Sequence[str]) -> tuple[dict[str, int], list[str]]:
        counts: dict[str, int] = {}
        remaining: list[str] = []
        legacy_to_worker = {str(meta["legacy_flag"]): worker for worker, meta in WORKERS.items()}

        i = 0
        while i < len(args):
            arg = args[i]
            if arg in legacy_to_worker:
                if i + 1 >= len(args):
                    self.ui.error(f"{arg} requires a value.")
                    raise SystemExit(2)
                counts[legacy_to_worker[arg]] = self.parse_count(args[i + 1])
                i += 2
                continue
            if arg.startswith("--scale-") and "=" in arg:
                flag, value = arg.split("=", 1)
                worker = legacy_to_worker.get(flag)
                if worker:
                    counts[worker] = self.parse_count(value)
                    i += 1
                    continue
            remaining.append(arg)
            i += 1

        return counts, remaining

    def parse_count_pairs(self, tokens: Sequence[str]) -> dict[str, int]:
        counts: dict[str, int] = {}
        if not tokens:
            return counts

        for token in tokens:
            if "=" not in token:
                self.ui.warn(f"Ignoring scale token without '=': {token}")
                continue
            raw_worker, raw_count = token.split("=", 1)
            worker = self.normalize_worker_name(raw_worker)
            counts[worker] = self.parse_count(raw_count)
        return counts

    def parse_count(self, raw: str) -> int:
        try:
            value = int(raw)
        except ValueError:
            raise SystemExit(f"Worker counts must be non-negative integers; got {raw!r}.")
        if value < 0:
            raise SystemExit("Worker counts must be non-negative.")
        return value

    def normalize_worker_counts(
        self,
        counts: Mapping[str, int],
        *,
        include_http_requester: bool = True,
    ) -> dict[str, int]:
        normalized: dict[str, int] = {}
        for raw_worker, count in counts.items():
            worker = self.normalize_worker_name(raw_worker)
            if worker == "worker-http-requester" and not include_http_requester:
                continue
            if worker not in WORKERS:
                raise SystemExit(f"Unknown worker: {raw_worker}")
            normalized[worker] = int(count)
        return normalized

    def normalize_worker_name(self, raw: str) -> str:
        key = raw.strip().lower().replace("_", "-")
        if key in WORKERS:
            return key
        if not key.startswith("worker-"):
            candidate = f"worker-{key}"
            if candidate in WORKERS:
                return candidate
        for worker, meta in WORKERS.items():
            if key == str(meta["short"]).lower():
                return worker
        raise SystemExit(f"Unknown worker '{raw}'. Valid workers: {', '.join(WORKERS)}")

    def ecs_service_name(self, worker: str) -> str:
        meta = WORKERS[worker]
        env_name = str(meta["ecs_env"])
        return self.get_env(env_name, str(meta["ecs_default"]))

    def get_env(self, key: str, default: str = "") -> str:
        return os.environ.get(key) or self.env.get(key) or default

    # ---------- help/preflight ----------

    def print_help(self) -> None:
        print(
            """
Argus deployment console

Usage:
  ./deploy [global options] [command]

Interactive:
  ./deploy
  ./deploy menu

Deploy/update:
  ./deploy all-in-1 [service...]            Fast path: compare per-service source update dates, rebuild changed artifacts only
  ./deploy all-in-1 --no-update [service...] Compare without fast-forwarding the local checkout
  ./deploy all-in-1 --no-fetch [service...]  Use the already-fetched GitHub ref
  ./deploy all-in-1 --no-github-artifacts    Disable downloading prebuilt image artifacts from GitHub Actions
  ./deploy all-in-1 --no-remote-images       Disable pulling prebuilt images from remote registry
  ./deploy deploy --hot [service...]
  ./deploy deploy --image [service...]
  ./deploy -fast, --fast [service...]        Non-interactive: runs all-in-1 deploy without showing the menu
  ./deploy deploy --fresh
  ./deploy deploy --ecs-workers
  ./deploy deploy --gcp-workers

Scaling:
  ./deploy scale local worker-spider=4 worker-enum=2 worker-http-requester=2
  ./deploy scale ecs worker-spider=6 worker-techid=1
  ./deploy scale gcp worker-spider=2:10 worker-enum=2
  ./deploy scale autoscale

Monitoring:
  ./deploy preflight
  ./deploy monitor
  ./deploy status [service...]
  ./deploy logs [--follow] [service...]
  ./deploy logs --errors [service...]
  ./deploy health
  ./deploy changed
  ./deploy services
  ./deploy validate [--ci]

AWS/ECS:
  ./deploy ecs hybrid
  ./deploy ecs repos
  ./deploy ecs build [service...]
  ./deploy ecs deploy [service...]
  ./deploy ecs release [service...]
  ./deploy ecs replace [worker-service...]
  ./deploy ecs status

Google Cloud Run:
  ./deploy gcp configure
  ./deploy gcp provision
  ./deploy gcp build [worker...]
  ./deploy gcp deploy [worker...]
  ./deploy gcp release [worker...]
  ./deploy gcp scale [worker=min:max|worker=count ...]
  ./deploy gcp status [worker...]
  ./deploy gcp teardown [worker...]

Global options:
  --dry-run, -n       Print commands without executing them
  --yes, -y          Assume yes for destructive confirmations
  --repo-root PATH   Run against a specific checkout
  --no-color         Disable ANSI color output
  --verbose          Print discovery commands too

Compatibility:
  deploy.py still accepts the historical deployment shortcuts:
  up, --fresh, -fresh, --ecs-workers, --gcp-workers, --hot, --image, logs, status, restart, down, smoke.
""".strip()
        )

    def preflight(self) -> int:
        self.ui.header("Preflight")
        self.show_context()
        failures = 0

        checks = [
            ("Python", sys.executable),
            ("Docker", "docker"),
            ("Git", "git"),
            ("AWS CLI", "aws"),
            ("gcloud", "gcloud"),
        ]

        for name, exe in checks:
            found = which(exe) if exe != sys.executable else exe
            if found:
                self.ui.ok(f"{name:<10} {found}")
            else:
                failures += 1
                self.ui.warn(f"{name:<10} not found")

        compose_code, compose_out, _ = self.runner.capture(["docker", "compose", "version"])
        if compose_code == 0:
            self.ui.ok(f"Compose    {compose_out.strip()}")
        elif which("docker-compose"):
            code, out, _ = self.runner.capture(["docker-compose", "--version"])
            self.ui.ok(f"Compose    {out.strip() if code == 0 else 'docker-compose'}")
        else:
            failures += 1
            self.ui.warn("Compose    not found")

        for path in [self.paths.compose_file, self.paths.repo_root / "deploy.py", self.paths.repo_root / "deploy"]:
            if path.exists():
                self.ui.ok(f"File       {self.paths.rel(path)}")
            else:
                failures += 1
                self.ui.warn(f"File       missing: {self.paths.rel(path)}")

        return 1 if failures else 0


def parse_global_options(argv: Sequence[str]) -> tuple[Options, list[str]]:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("-n", "--dry-run", action="store_true")
    parser.add_argument("-y", "--yes", action="store_true")
    parser.add_argument("--no-color", action="store_true")
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("-h", "--help", action="store_true")

    ns, remaining = parser.parse_known_args(argv)
    options = Options(
        dry_run=ns.dry_run,
        assume_yes=ns.yes,
        no_color=ns.no_color,
        repo_root=ns.repo_root,
        verbose=ns.verbose,
    )
    if ns.help and not remaining:
        remaining = ["help"]
    return options, remaining


def main(argv: Sequence[str]) -> int:
    options, remaining = parse_global_options(argv)
    paths = Paths.resolve(options.repo_root)
    console = ArgusDeployConsole(paths, options)
    return console.run(remaining)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
