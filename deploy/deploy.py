#!/usr/bin/env python3
"""
Argus Engine deployment console.

This script intentionally uses only the Python standard library so it can run on
fresh EC2/local hosts before project dependencies are installed.

It is both:
  1. a friendly menu-driven CLI for humans, and
  2. the single Python source of truth for deployment operations.

Examples:
    ./deploy/deploy.py
    ./deploy/deploy.py deploy --hot
    ./deploy/deploy.py deploy --image command-center-web worker-spider
    ./deploy/deploy.py scale local worker-spider=4 worker-enum=2
    ./deploy/deploy.py scale ecs worker-spider=6 worker-techid=1
    ./deploy/deploy.py monitor
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
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Optional, Sequence


# Keep this list in sync with deploy/docker-compose.yml. The script will prefer
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
}

PROJECT_HINTS = {
    "command-center-gateway": ["src/ArgusEngine.CommandCenter.Gateway/"],
    "command-center-operations-api": ["src/ArgusEngine.CommandCenter.Operations.Api/"],
    "command-center-discovery-api": ["src/ArgusEngine.CommandCenter.Discovery.Api/"],
    "command-center-worker-control-api": ["src/ArgusEngine.CommandCenter.WorkerControl.Api/"],
    "command-center-maintenance-api": ["src/ArgusEngine.CommandCenter.Maintenance.Api/"],
    "command-center-updates-api": ["src/ArgusEngine.CommandCenter.Updates.Api/"],
    "command-center-realtime": ["src/ArgusEngine.CommandCenter.Realtime.Host/"],
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
    "deploy/docker-compose.yml",
    "deploy/Dockerfile.base-runtime",
    "deploy/Dockerfile.base-recon",
    "deploy/Dockerfile.commandcenter-host",
    "deploy/Dockerfile.worker",
    "deploy/Dockerfile.worker-enum",
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
            deploy_dir = candidate / "deploy"
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

        self.ui.error("Docker Compose was not found. Install Docker Compose v2 and rerun deploy/deploy.py.")
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

        if command in {"deploy", "update"}:
            return self.deploy_from_args(rest)
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
                "What do you want to do?",
                [
                    "Deploy or update the local Docker Compose stack",
                    "Scale local or ECS workers",
                    "Monitor health, status, logs, queues, and worker counts",
                    "Operate services: restart, stop, smoke test, clean",
                    "Google Cloud Run worker deployment and scaling",
                    "AWS ECS / ECR deployment and monitoring",
                    "Show changed/affected services",
                    "Exit",
                ],
            )

            if choice == 0:
                code = self.deploy_menu()
            elif choice == 1:
                code = self.scale_menu()
            elif choice == 2:
                code = self.monitor_menu()
            elif choice == 3:
                code = self.operations_menu()
            elif choice == 4:
                code = self.gcp_menu()
            elif choice == 5:
                code = self.ecs_menu()
            elif choice == 6:
                self.show_changed_services()
                code = 0
            else:
                return 0

            if code != 0:
                self.ui.warn(f"Last action exited with code {code}.")
            self.ui.pause()

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

        service_env = Path(self.get_env("SERVICE_ENV_FILE", "deploy/gcp/service-env"))
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
        service_env = Path(self.get_env("SERVICE_ENV_FILE", "deploy/gcp/service-env"))
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
        env_file = Path(self.get_env("SERVICE_ENV_FILE", "deploy/gcp/service-env"))
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
  deploy/deploy.py gcp configure
  deploy/deploy.py gcp provision
  deploy/deploy.py gcp build [worker...]
  deploy/deploy.py gcp deploy [worker...]
  deploy/deploy.py gcp release [worker...]
  deploy/deploy.py gcp scale [worker=min:max|worker=count ...]
  deploy/deploy.py gcp status [worker...]
  deploy/deploy.py gcp teardown [worker...]
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

    def service_catalog(self) -> dict[str, dict[str, str]]:
        catalog_path = self.paths.deploy_dir / "service-catalog.tsv"
        services: dict[str, dict[str, str]] = {}
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
                services[row["service"]] = row
        return services

    def selected_ecr_services(self, args: Sequence[str]) -> list[dict[str, str]]:
        catalog = self.service_catalog()
        requested = [arg for arg in args if arg and arg != "all"]
        names = requested or [name for name, row in catalog.items() if row.get("ecr_enabled") == "1"]
        missing = [name for name in names if name not in catalog]
        if missing:
            raise SystemExit(f"Unknown ECR service(s): {', '.join(missing)}")
        return [catalog[name] for name in names if catalog[name].get("ecr_enabled") == "1"]

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
        if any(file in GLOBAL_INVALIDATORS or file.startswith("deploy/") for file in normalized):
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
  deploy/deploy.py [global options] [command]

Interactive:
  deploy/deploy.py
  deploy/deploy.py menu

Deploy/update:
  deploy/deploy.py deploy --hot [service...]
  deploy/deploy.py deploy --image [service...]
  deploy/deploy.py deploy --fresh
  deploy/deploy.py deploy --ecs-workers
  deploy/deploy.py deploy --gcp-workers

Scaling:
  deploy/deploy.py scale local worker-spider=4 worker-enum=2 worker-http-requester=2
  deploy/deploy.py scale ecs worker-spider=6 worker-techid=1
  deploy/deploy.py scale gcp worker-spider=2:10 worker-enum=2
  deploy/deploy.py scale autoscale

Monitoring:
  deploy/deploy.py preflight
  deploy/deploy.py monitor
  deploy/deploy.py status [service...]
  deploy/deploy.py logs [--follow] [service...]
  deploy/deploy.py logs --errors [service...]
  deploy/deploy.py health
  deploy/deploy.py changed
  deploy/deploy.py services
  deploy/deploy.py validate [--ci]

AWS/ECS:
  deploy/deploy.py ecs hybrid
  deploy/deploy.py ecs repos
  deploy/deploy.py ecs build [service...]
  deploy/deploy.py ecs deploy [service...]
  deploy/deploy.py ecs release [service...]
  deploy/deploy.py ecs replace [worker-service...]
  deploy/deploy.py ecs status

Google Cloud Run:
  deploy/deploy.py gcp configure
  deploy/deploy.py gcp provision
  deploy/deploy.py gcp build [worker...]
  deploy/deploy.py gcp deploy [worker...]
  deploy/deploy.py gcp release [worker...]
  deploy/deploy.py gcp scale [worker=min:max|worker=count ...]
  deploy/deploy.py gcp status [worker...]
  deploy/deploy.py gcp teardown [worker...]

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

        for path in [self.paths.compose_file, self.paths.deploy_dir / "deploy.py"]:
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
