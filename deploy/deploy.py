#!/usr/bin/env python3
"""
Argus Engine deployment console.

This script intentionally uses only the Python standard library so it can run on
fresh EC2/local hosts before project dependencies are installed.

It is both:
  1. a friendly menu-driven CLI for humans, and
  2. a backwards-compatible deploy.sh handoff target.

Examples:
    ./deploy/deploy-ui.py
    ./deploy/deploy-ui.py deploy --hot
    ./deploy/deploy-ui.py deploy --image command-center-web worker-spider
    ./deploy/deploy-ui.py scale local worker-spider=4 worker-enum=2
    ./deploy/deploy-ui.py scale ecs worker-spider=6 worker-techid=1
    ./deploy/deploy-ui.py monitor
"""

from __future__ import annotations

import argparse
import os
import re
import shlex
import shutil
import subprocess
import sys
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
    deploy_sh: Path
    logs_sh: Path
    smoke_test: Path
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
                    deploy_sh=deploy_dir / "deploy.sh",
                    logs_sh=deploy_dir / "logs.sh",
                    smoke_test=deploy_dir / "smoke-test.sh",
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

    def shell(self, command: str) -> int:
        if os.name == "nt":
            return self.run(["powershell", "-NoProfile", "-Command", command])
        return self.run(["bash", "-lc", command])

    def bash_script(self, path: Path, args: Sequence[str] = (), *, env: Optional[Mapping[str, str]] = None) -> int:
        if not path.exists():
            self.ui.error(f"Script not found: {self.paths.rel(path)}")
            return 2
        return self.run(["bash", str(path), *args], env=env)


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

        self.ui.error("Docker Compose was not found. Install Docker Compose v2 or run deploy.sh so it can bootstrap dependencies.")
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

        # Compatibility with deploy.sh's historical handoff behavior.
        if command in {"up"}:
            return self.deploy_from_args(rest)
        if command in {"--fresh", "-fresh"}:
            return self.deploy_sh(["--fresh", *rest])
        if command in {"--ecs-workers"}:
            return self.deploy_sh(["--ecs-workers", *rest])
        if command in {"--hot", "-hot"}:
            return self.deploy_sh(["--hot", *rest])
        if command in {"--image", "-image"}:
            return self.deploy_sh(["--image", *rest])

        if command in {"deploy", "update"}:
            return self.deploy_from_args(rest)
        if command in {"scale", "workers", "worker"}:
            return self.scale_from_args(rest)
        if command in {"monitor", "status", "ps"}:
            return self.monitor_from_args([command, *rest])
        if command in {"logs", "log"}:
            return self.logs_from_args(rest)
        if command == "restart":
            return self.deploy_sh(["restart", *rest])
        if command == "down":
            return self.deploy_sh(["down", *rest])
        if command == "smoke":
            return self.deploy_sh(["smoke", *rest])
        if command == "clean":
            return self.clean()
        if command in {"ecs", "aws", "cloud"}:
            return self.ecs_from_args(rest)
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
                    "AWS ECS / ECR deployment and monitoring",
                    "Show changed/affected services",
                    "Open a command shell from the repo root",
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
                code = self.ecs_menu()
            elif choice == 5:
                self.show_changed_services()
                code = 0
            elif choice == 6:
                code = self.custom_shell()
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
                "Deploy local core + ECS workers from this EC2 host",
                "Back",
            ],
        )
        if choice == 0:
            return self.deploy_sh(["--hot"])
        if choice == 1:
            return self.deploy_sh(["--image"])
        if choice == 2:
            return self.deploy_sh(["--fresh"])
        if choice == 3:
            return self.selected_component_deploy()
        if choice == 4:
            return self.deploy_sh(["--ecs-workers"])
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
            return self.deploy_sh(["restart", *services])
        return self.deploy_sh(["logs", "--tail", "200", *services])

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
            return self.run_aws_script("autoscale-ecs-workers.sh")
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
            return self.deploy_sh(["status"])
        if choice == 1:
            self.show_worker_counts()
            return 0
        if choice == 2:
            return self.health_checks()
        if choice == 3:
            services = self.select_services(include_infra=True, allow_empty=True, default="none")
            tail = self.ui.prompt_int("Log tail lines", 200, minimum=1)
            return self.deploy_sh(["logs", "--tail", str(tail), *services])
        if choice == 4:
            services = self.select_services(include_infra=True, allow_empty=True, default="none")
            return self.deploy_sh(["logs", "--follow", *services])
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
            return self.deploy_sh(["restart", *services])
        if choice == 1:
            return self.deploy_sh(["restart"])
        if choice == 2:
            if self.ui.confirm("Stop the local stack?", default=False, assume_yes=self.options.assume_yes):
                return self.deploy_sh(["down"])
            return 0
        if choice == 3:
            return self.deploy_sh(["smoke"])
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
            return self.deploy_sh(["--ecs-workers"])
        if choice == 1:
            return self.run_aws_script("create-ecr-repos.sh")
        if choice in {2, 3, 4, 5}:
            services = self.select_services(include_infra=False, only_cloudish=True, default="workers")
            if not services:
                self.ui.warn("No services selected.")
                return 0
            if choice == 2:
                return self.run_aws_script("build-push-ecr.sh", services)
            if choice == 3:
                return self.run_aws_script("deploy-ecs-services.sh", services)
            if choice == 4:
                create = self.run_aws_script("create-ecr-repos.sh")
                if create != 0:
                    return create
                build = self.run_aws_script("build-push-ecr.sh", services)
                if build != 0:
                    return build
                return self.run_aws_script("deploy-ecs-services.sh", services)
            return self.run_aws_script("replace-ecs-worker-tasks.sh", services)
        if choice == 6:
            return self.run_aws_script("autoscale-ecs-workers.sh")
        if choice == 7:
            return self.ecs_status()
        return 0

    # ---------- direct commands ----------

    def deploy_from_args(self, args: Sequence[str]) -> int:
        args = list(args)
        scale_counts, remaining = self.extract_scale_args(args)

        if "--ecs-workers" in remaining:
            code = self.deploy_sh(["--ecs-workers", *[a for a in remaining if a != "--ecs-workers"]])
        elif "--fresh" in remaining or "-fresh" in remaining:
            code = self.deploy_sh(["--fresh", *[a for a in remaining if a not in {"--fresh", "-fresh"}]])
        elif "--image" in remaining or "-image" in remaining:
            services = [a for a in remaining if not a.startswith("-")]
            if services:
                code = self.runner.run(self.compose("build", *services))
                if code == 0:
                    code = self.runner.run(self.compose("up", "-d", "--no-deps", *services))
            else:
                code = self.deploy_sh(["--image"])
        else:
            services = [a for a in remaining if not a.startswith("-")]
            code = self.deploy_sh(["--hot", *services])

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
        if target in {"autoscale", "auto"}:
            return self.run_aws_script("autoscale-ecs-workers.sh", args[1:])

        # Default to local for convenience: deploy-ui.py scale worker-spider=3
        counts = self.parse_count_pairs(args)
        return self.apply_local_worker_scale(counts)

    def monitor_from_args(self, args: Sequence[str]) -> int:
        command = args[0]
        rest = list(args[1:])
        if command in {"status", "ps"}:
            return self.deploy_sh(["status", *rest])
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
        return self.deploy_sh(["logs", *args])

    def ecs_from_args(self, args: Sequence[str]) -> int:
        if not args:
            return self.ecs_menu()
        action = args[0].lower()
        rest = list(args[1:])

        if action in {"hybrid", "workers"}:
            return self.deploy_sh(["--ecs-workers", *rest])
        if action == "repos":
            return self.run_aws_script("create-ecr-repos.sh", rest)
        if action in {"build", "push"}:
            return self.run_aws_script("build-push-ecr.sh", rest)
        if action == "deploy":
            return self.run_aws_script("deploy-ecs-services.sh", rest)
        if action == "release":
            create = self.run_aws_script("create-ecr-repos.sh")
            if create != 0:
                return create
            build = self.run_aws_script("build-push-ecr.sh", rest)
            if build != 0:
                return build
            return self.run_aws_script("deploy-ecs-services.sh", rest)
        if action == "replace":
            return self.run_aws_script("replace-ecs-worker-tasks.sh", rest)
        if action == "autoscale":
            return self.run_aws_script("autoscale-ecs-workers.sh", rest)
        if action == "scale":
            return self.apply_ecs_worker_scale(self.parse_count_pairs(rest))
        if action == "status":
            return self.ecs_status()

        self.ui.error(f"Unknown ECS action: {action}")
        return 2

    # ---------- action helpers ----------

    def deploy_sh(self, args: Sequence[str]) -> int:
        return self.runner.bash_script(self.paths.deploy_sh, list(args))

    def clean(self) -> int:
        self.ui.warn("This removes compose containers, orphans, volumes, and hot-publish output.")
        if not self.ui.confirm("Continue with clean?", default=False, assume_yes=self.options.assume_yes):
            return 0
        return self.runner.bash_script(
            self.paths.deploy_sh,
            ["clean"],
            env={"CONFIRM_ARGUS_CLEAN": "yes"},
        )

    def run_aws_script(self, name: str, args: Sequence[str] = ()) -> int:
        script = self.paths.aws_dir / name
        if not script.exists():
            self.ui.error(f"AWS helper not found: {self.paths.rel(script)}")
            return 2
        return self.runner.bash_script(script, list(args))

    def health_checks(self) -> int:
        self.ui.section("Health checks")
        failures = 0
        for service, url in HEALTH_ENDPOINTS.items():
            start = time.time()
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "argus-deploy-ui/1.0"})
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
        if self.paths.logs_sh.exists():
            return self.runner.bash_script(self.paths.logs_sh, ["--errors", "--tail", str(tail), *services])
        return self.deploy_sh(["logs", "--tail", str(tail), *services])

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
        status_script = self.paths.aws_dir / "ecs-command-center-status.sh"
        if status_script.exists():
            return self.runner.bash_script(status_script)

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

    def custom_shell(self) -> int:
        command = self.ui.prompt("Command to run from repo root")
        if not command:
            return 0
        return self.runner.shell(command)

    # ---------- help/preflight ----------

    def print_help(self) -> None:
        print(
            """
Argus deployment console

Usage:
  deploy/deploy-ui.py [global options] [command]

Interactive:
  deploy/deploy-ui.py
  deploy/deploy-ui.py menu

Deploy/update:
  deploy/deploy-ui.py deploy --hot [service...]
  deploy/deploy-ui.py deploy --image [service...]
  deploy/deploy-ui.py deploy --fresh
  deploy/deploy-ui.py deploy --ecs-workers

Scaling:
  deploy/deploy-ui.py scale local worker-spider=4 worker-enum=2 worker-http-requester=2
  deploy/deploy-ui.py scale ecs worker-spider=6 worker-techid=1
  deploy/deploy-ui.py scale autoscale

Monitoring:
  deploy/deploy-ui.py monitor
  deploy/deploy-ui.py status [service...]
  deploy/deploy-ui.py logs [--follow] [service...]
  deploy/deploy-ui.py logs --errors [service...]
  deploy/deploy-ui.py health
  deploy/deploy-ui.py changed
  deploy/deploy-ui.py services

AWS/ECS:
  deploy/deploy-ui.py ecs hybrid
  deploy/deploy-ui.py ecs repos
  deploy/deploy-ui.py ecs build [service...]
  deploy/deploy-ui.py ecs deploy [service...]
  deploy/deploy-ui.py ecs release [service...]
  deploy/deploy-ui.py ecs replace [worker-service...]
  deploy/deploy-ui.py ecs status

Global options:
  --dry-run, -n       Print commands without executing them
  --yes, -y          Assume yes for destructive confirmations
  --repo-root PATH   Run against a specific checkout
  --no-color         Disable ANSI color output
  --verbose          Print discovery commands too

Compatibility:
  deploy-ui.py still accepts deploy.sh handoff-style arguments such as:
  up, --fresh, -fresh, --ecs-workers, --hot, --image, logs, status, restart, down, smoke.
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
            ("Bash", "bash"),
            ("AWS CLI", "aws"),
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

        for path in [self.paths.compose_file, self.paths.deploy_sh, self.paths.logs_sh]:
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
