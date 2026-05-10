#!/usr/bin/env python3
"""
Create a changes-only zip for derekdperez/argus-engine containing the updated
deploy/deploy-ui.py.
"""

from __future__ import annotations

from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

DEPLOY_UI = r'''#!/usr/bin/env python3
"""
Argus Engine interactive deployment menu.

This file intentionally has no third-party Python dependencies. It replaces the
old compatibility shim with a full interactive console that can:

- monitor local/cloud deployments
- compare deployed component versions with GitHub/main
- update one component or every component with an available update
- run the existing local, AWS, Azure, and GCP deployment helpers
- configure cloud credentials and provider env files once
- control worker provisioning and scaling through the deployment configuration
"""

from __future__ import annotations

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import textwrap
import time
import urllib.error
import urllib.request
import webbrowser
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence


REPO_OWNER = "derekdperez"
REPO_NAME = "argus-engine"
REPO_URL = f"https://github.com/{REPO_OWNER}/{REPO_NAME}"
RAW_BASE_URL = f"https://raw.githubusercontent.com/{REPO_OWNER}/{REPO_NAME}/main"
GITHUB_COMMIT_API = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/commits/main"

CORE_SERVICES = [
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
]

WORKER_SERVICES = [
    "worker-spider",
    "worker-http-requester",
    "worker-enum",
    "worker-portscan",
    "worker-highvalue",
    "worker-techid",
]

ALL_KNOWN_SERVICES = CORE_SERVICES + WORKER_SERVICES


@dataclass(frozen=True)
class LatestRelease:
    version: str
    sha: str
    source: str


@dataclass(frozen=True)
class ServiceStatus:
    name: str
    deployed: bool
    state: str
    health: str
    version: str
    revision: str
    image: str
    update_available: bool
    reason: str


class CommandRunner:
    def __init__(self, root: Path, *, dry_run: bool = False) -> None:
        self.root = root
        self.dry_run = dry_run

    def call(
        self,
        args: Sequence[str],
        *,
        env: dict[str, str] | None = None,
        cwd: Path | None = None,
        quiet: bool = False,
    ) -> int:
        if not quiet:
            print_cmd(args)
        if self.dry_run:
            return 0
        try:
            return subprocess.call(list(args), cwd=str(cwd or self.root), env=env)
        except FileNotFoundError:
            print_error(f"Command not found: {args[0]}")
            return 127

    def output(
        self,
        args: Sequence[str],
        *,
        env: dict[str, str] | None = None,
        cwd: Path | None = None,
        quiet: bool = True,
    ) -> str:
        if not quiet:
            print_cmd(args)
        if self.dry_run:
            return ""
        try:
            completed = subprocess.run(
                list(args),
                cwd=str(cwd or self.root),
                env=env,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                check=False,
            )
        except FileNotFoundError:
            return ""
        return completed.stdout.strip()


class ArgusDeployUi:
    def __init__(self, root: Path, *, dry_run: bool = False, assume_yes: bool = False) -> None:
        self.root = root
        self.deploy_dir = root / "deploy"
        self.runner = CommandRunner(root, dry_run=dry_run)
        self.dry_run = dry_run
        self.assume_yes = assume_yes

    @property
    def compose_file(self) -> Path:
        return self.deploy_dir / "docker-compose.yml"

    def compose_cmd(self) -> list[str]:
        if shutil.which("docker"):
            return ["docker", "compose", "-f", str(self.compose_file)]
        return ["docker-compose", "-f", str(self.compose_file)]

    def deploy_script(self, args: Sequence[str]) -> int:
        env = os.environ.copy()
        env["ARGUS_NO_UI"] = "1"
        script = self.deploy_dir / "deploy.sh"
        return self.runner.call(["bash", str(script), *args], env=env)

    def provider_env(self, provider: str) -> dict[str, str]:
        env = os.environ.copy()
        for path in [
            self.deploy_dir / ".env.local",
            self.deploy_dir / provider / ".env",
            self.deploy_dir / provider / ".env.generated",
        ]:
            env.update(read_env(path))
        return env

    def script(self, provider: str, name: str) -> Path:
        return self.deploy_dir / provider / name

    def run_script(self, provider: str, name: str, args: Sequence[str] = ()) -> int:
        path = self.script(provider, name)
        if not path.exists():
            print_error(f"Missing script: {path.relative_to(self.root)}")
            return 2
        return self.runner.call(["bash", str(path), *args], env=self.provider_env(provider))

    def main_menu(self) -> int:
        while True:
            clear_screen()
            self.print_header()
            choice = choose(
                "Main menu",
                [
                    "Monitor deployments and statuses",
                    "Version/update center",
                    "Deploy/update local stack",
                    "Update individual components",
                    "Update all components with available updates",
                    "AWS worker deployment / provisioning",
                    "Azure worker deployment / provisioning",
                    "Google Cloud worker deployment / provisioning",
                    "One-time credentials and provider configuration",
                    "Scale workers",
                    "Logs",
                    "Open Command Center web URLs",
                    "Run existing .NET DeployUi",
                    "Exit",
                ],
            )

            if choice == 0:
                self.monitor_menu()
            elif choice == 1:
                self.update_center()
            elif choice == 2:
                self.local_menu()
            elif choice == 3:
                self.update_individual()
            elif choice == 4:
                self.update_all_available()
            elif choice == 5:
                self.aws_menu()
            elif choice == 6:
                self.azure_menu()
            elif choice == 7:
                self.gcp_menu()
            elif choice == 8:
                self.config_wizard()
            elif choice == 9:
                self.scale_menu()
            elif choice == 10:
                self.logs_menu()
            elif choice == 11:
                self.open_web_urls()
            elif choice == 12:
                self.run_dotnet_ui([])
            else:
                return 0

    def print_header(self) -> None:
        latest = self.latest_release()
        local = self.local_version()
        statuses = self.service_statuses(latest, quiet=True)
        updates = [s for s in statuses if s.update_available]
        print_box(
            "Argus Engine Deployment Center",
            [
                f"Repo:          {REPO_URL}",
                f"Root:          {self.root}",
                f"Local version: {local or 'unknown'}",
                f"GitHub/main:   {latest.version or 'unknown'} ({short_sha(latest.sha)})",
                f"Updates:       {len(updates)} deployed component(s) behind GitHub/main",
                f"Mode:          {'dry-run' if self.dry_run else 'live'}",
            ],
        )
        if updates:
            print_warn(
                "Update alert: "
                + ", ".join(s.name for s in updates[:8])
                + (" ..." if len(updates) > 8 else "")
            )

    def monitor_menu(self) -> None:
        while True:
            clear_screen()
            print_title("Deployment monitor")
            self.status_all()
            print()
            action = prompt("Press Enter to refresh, 'q' to go back, or seconds to auto-refresh once", "")
            if action.lower() in {"q", "quit", "back"}:
                return
            if action.isdigit():
                time.sleep(max(1, int(action)))

    def update_center(self) -> None:
        while True:
            clear_screen()
            print_title("Version/update center")
            self.print_versions()
            choice = choose(
                "Update actions",
                [
                    "Refresh",
                    "Update one component",
                    "Update all available",
                    "Show known services",
                    "Back",
                ],
            )
            if choice == 0:
                continue
            if choice == 1:
                self.update_individual()
            elif choice == 2:
                self.update_all_available()
            elif choice == 3:
                self.show_services()
                pause()
            else:
                return

    def local_menu(self) -> None:
        choice = choose(
            "Local Docker Compose deployment",
            [
                "Incremental hot deploy",
                "Image deploy",
                "Fresh no-cache rebuild",
                "Status",
                "Restart services",
                "Logs",
                "Smoke test",
                "Down",
                "Clean volumes",
                "Back",
            ],
        )

        if choice == 0:
            self.deploy_script(["--hot"])
        elif choice == 1:
            self.deploy_script(["--image"])
        elif choice == 2:
            self.deploy_script(["--fresh"])
        elif choice == 3:
            self.local_status()
        elif choice == 4:
            services = self.select_services(allow_all=True)
            self.deploy_script(["restart", *services])
        elif choice == 5:
            self.logs_menu()
        elif choice == 6:
            self.deploy_script(["smoke"])
        elif choice == 7:
            self.deploy_script(["down"])
        elif choice == 8:
            if confirm("Remove compose volumes?", default=False, assume_yes=self.assume_yes):
                env = os.environ.copy()
                env["CONFIRM_ARGUS_CLEAN"] = "yes"
                env["ARGUS_NO_UI"] = "1"
                self.runner.call(["bash", str(self.deploy_dir / "deploy.sh"), "clean"], env=env)
        pause()

    def logs_menu(self) -> None:
        services = self.select_services(allow_all=True, allow_empty=True)
        tail = prompt("Tail lines", "200")
        follow = confirm("Follow logs?", default=False, assume_yes=False)
        args = ["logs", "--tail", tail]
        if follow:
            args.append("--follow")
        args.extend(services)
        self.deploy_script(args)
        if not follow:
            pause()

    def aws_menu(self) -> None:
        choice = choose(
            "AWS ECS/ECR + EC2 workers",
            [
                "Configure AWS credentials/env",
                "EC2 hybrid release: local core + ECS workers",
                "Create ECR repositories/resources",
                "Build and push selected ECR images",
                "Deploy selected ECS services",
                "Build, push, and deploy selected ECS services",
                "Replace selected ECS worker tasks",
                "Run ECS autoscale pass",
                "Provision EC2 worker instances",
                "Deploy to existing EC2 worker instance IDs",
                "Scale ECS workers",
                "Show AWS status",
                "Back",
            ],
        )
        if choice == 0:
            self.configure_aws()
        elif choice == 1:
            self.deploy_script(["--ecs-workers"])
        elif choice == 2:
            self.run_script("aws", "create-ecr-repos.sh")
        elif choice == 3:
            self.run_script("aws", "build-push-ecr.sh", self.select_cloud_services("aws"))
        elif choice == 4:
            self.run_script("aws", "deploy-ecs-services.sh", self.select_cloud_services("aws"))
        elif choice == 5:
            services = self.select_cloud_services("aws")
            if self.run_script("aws", "create-ecr-repos.sh") == 0:
                if self.run_script("aws", "build-push-ecr.sh", services) == 0:
                    self.run_script("aws", "deploy-ecs-services.sh", services)
        elif choice == 6:
            self.run_script("aws", "replace-ecs-worker-tasks.sh", self.select_worker_services())
        elif choice == 7:
            self.run_script("aws", "autoscale-ecs-workers.sh")
        elif choice == 8:
            self.run_script("aws", "provision-ec2-workers.sh")
        elif choice == 9:
            ids = prompt("Instance IDs, space-separated", "").split()
            self.run_script("aws", "deploy-worker-instances.sh", ids)
        elif choice == 10:
            self.scale_provider("aws")
        elif choice == 11:
            self.aws_status()
        pause()

    def azure_menu(self) -> None:
        choice = choose(
            "Azure Container Apps / ACR",
            [
                "Configure Azure credentials/env",
                "Create Azure Container Apps resources",
                "Build and push selected ACR images",
                "Deploy selected Container Apps workers",
                "Build, push, and deploy selected services",
                "Scale Azure workers",
                "Show Azure status",
                "Back",
            ],
        )
        if choice == 0:
            self.configure_azure()
        elif choice == 1:
            self.run_script("azure", "create-containerapps-resources.sh")
        elif choice == 2:
            self.run_script("azure", "build-push-acr.sh", self.select_cloud_services("azure"))
        elif choice == 3:
            self.run_script("azure", "deploy-containerapps-workers.sh", self.select_cloud_services("azure"))
        elif choice == 4:
            services = self.select_cloud_services("azure")
            if self.run_script("azure", "create-containerapps-resources.sh") == 0:
                if self.run_script("azure", "build-push-acr.sh", services) == 0:
                    self.run_script("azure", "deploy-containerapps-workers.sh", services)
        elif choice == 5:
            self.scale_provider("azure")
        elif choice == 6:
            self.azure_status()
        pause()

    def gcp_menu(self) -> None:
        choice = choose(
            "Google Cloud Run Worker Pools / Artifact Registry",
            [
                "Configure Google Cloud credentials/env",
                "Create Artifact Registry repository",
                "Build and push selected Artifact Registry images",
                "Deploy selected Cloud Run worker pools",
                "Build, push, and deploy selected services",
                "Scale GCP workers",
                "Show GCP status",
                "Back",
            ],
        )
        if choice == 0:
            self.configure_gcp()
        elif choice == 1:
            self.run_script("gcp", "create-artifact-registry.sh")
        elif choice == 2:
            self.run_script("gcp", "build-push-artifact-registry.sh", self.select_cloud_services("gcp"))
        elif choice == 3:
            self.run_script("gcp", "deploy-cloudrun-worker-pools.sh", self.select_cloud_services("gcp"))
        elif choice == 4:
            services = self.select_cloud_services("gcp")
            if self.run_script("gcp", "create-artifact-registry.sh") == 0:
                if self.run_script("gcp", "build-push-artifact-registry.sh", services) == 0:
                    self.run_script("gcp", "deploy-cloudrun-worker-pools.sh", services)
        elif choice == 5:
            self.scale_provider("gcp")
        elif choice == 6:
            self.gcp_status()
        pause()

    def scale_menu(self) -> None:
        provider = choose("Scale provider", ["AWS ECS", "Azure Container Apps", "Google Cloud Run Worker Pools", "Back"])
        if provider == 0:
            self.scale_provider("aws")
        elif provider == 1:
            self.scale_provider("azure")
        elif provider == 2:
            self.scale_provider("gcp")
        pause()

    def status_all(self) -> int:
        local = self.local_status()
        print()
        aws = self.aws_status(quiet_missing=True)
        print()
        azure = self.azure_status(quiet_missing=True)
        print()
        gcp = self.gcp_status(quiet_missing=True)
        return first_nonzero(local, aws, azure, gcp)

    def local_status(self) -> int:
        print_title("Local Docker Compose")
        return self.runner.call([*self.compose_cmd(), "ps"])

    def aws_status(self, *, quiet_missing: bool = False) -> int:
        print_title("AWS")
        status_script = self.script("aws", "ecs-command-center-status.sh")
        if status_script.exists():
            return self.run_script("aws", "ecs-command-center-status.sh")
        env = self.provider_env("aws")
        cluster = env.get("ECS_CLUSTER", "argus-engine")
        if shutil.which("aws"):
            return self.runner.call(["aws", "ecs", "list-services", "--cluster", cluster, "--output", "table"], env=env)
        if not quiet_missing:
            print_warn("AWS CLI not found.")
        return 0

    def azure_status(self, *, quiet_missing: bool = False) -> int:
        print_title("Azure")
        doctor = self.script("azure", "doctor.sh")
        if doctor.exists():
            return self.run_script("azure", "doctor.sh")
        env = self.provider_env("azure")
        if shutil.which("az"):
            group = env.get("AZURE_RESOURCE_GROUP", "")
            args = ["az", "containerapp", "list", "--output", "table"]
            if group:
                args[3:3] = ["--resource-group", group]
            return self.runner.call(args, env=env)
        if not quiet_missing:
            print_warn("Azure CLI not found.")
        return 0

    def gcp_status(self, *, quiet_missing: bool = False) -> int:
        print_title("Google Cloud")
        env = self.provider_env("gcp")
        if shutil.which("gcloud"):
            project = env.get("GCP_PROJECT_ID") or env.get("GOOGLE_CLOUD_PROJECT")
            region = env.get("GCP_REGION") or env.get("GOOGLE_REGION", "us-central1")
            base = ["gcloud", "run", "worker-pools", "list", "--region", region]
            if project:
                base.extend(["--project", project])
            rc = self.runner.call(base, env=env)
            if rc != 0:
                beta = ["gcloud", "beta", "run", "worker-pools", "list", "--region", region]
                if project:
                    beta.extend(["--project", project])
                return self.runner.call(beta, env=env)
            return rc
        if not quiet_missing:
            print_warn("Google Cloud CLI not found.")
        return 0

    def print_versions(self) -> None:
        latest = self.latest_release()
        statuses = self.service_statuses(latest, quiet=False)
        print(f"GitHub/main version: {latest.version or 'unknown'}  commit: {short_sha(latest.sha)}")
        print(f"Local checkout version: {self.local_version() or 'unknown'}")
        print()
        print_table(
            ["Component", "State", "Health", "Deployed", "Git", "Update", "Reason"],
            [
                [
                    s.name,
                    s.state,
                    s.health,
                    s.version or "-",
                    latest.version or "-",
                    "YES" if s.update_available else "no",
                    s.reason,
                ]
                for s in statuses
            ],
        )

    def latest_release(self) -> LatestRelease:
        version = ""
        source = "GitHub raw VERSION"
        text = fetch_text(f"{RAW_BASE_URL}/VERSION")
        if text:
            version = text.strip().splitlines()[0].strip()
        if not version:
            source = "GitHub raw docker-compose.yml"
            compose = fetch_text(f"{RAW_BASE_URL}/deploy/docker-compose.yml")
            match = re.search(r"ARGUS_ENGINE_VERSION:-([^}\\s]+)", compose or "")
            version = match.group(1).strip() if match else ""
        sha = ""
        commit_json = fetch_text(GITHUB_COMMIT_API)
        if commit_json:
            try:
                sha = json.loads(commit_json).get("sha", "")
            except json.JSONDecodeError:
                sha = ""
        return LatestRelease(version=version, sha=sha, source=source)

    def local_version(self) -> str:
        version_file = self.root / "VERSION"
        if version_file.exists():
            value = version_file.read_text(encoding="utf-8", errors="ignore").strip().splitlines()
            if value and value[0].strip():
                return value[0].strip()

        if self.compose_file.exists():
            text = self.compose_file.read_text(encoding="utf-8", errors="ignore")
            match = re.search(r"ARGUS_ENGINE_VERSION:-([^}\\s]+)", text)
            if match:
                return match.group(1).strip()

        return ""

    def service_statuses(self, latest: LatestRelease | None = None, *, quiet: bool = True) -> list[ServiceStatus]:
        latest = latest or self.latest_release()
        services = self.compose_services()
        statuses: list[ServiceStatus] = []
        for service in services:
            statuses.append(self.inspect_service(service, latest))
        if not quiet and not statuses:
            print_warn("No Docker Compose services detected.")
        return statuses

    def compose_services(self) -> list[str]:
        output = self.runner.output([*self.compose_cmd(), "config", "--services"])
        services = [line.strip() for line in output.splitlines() if line.strip()]
        if services:
            ordered = [s for s in ALL_KNOWN_SERVICES if s in services]
            extra = [s for s in services if s not in ordered]
            return ordered + extra
        return list(ALL_KNOWN_SERVICES)

    def inspect_service(self, service: str, latest: LatestRelease) -> ServiceStatus:
        cid = first_line(self.runner.output([*self.compose_cmd(), "ps", "-q", service]))
        if not cid:
            return ServiceStatus(service, False, "not-running", "-", "", "", "", False, "not deployed")

        state = clean_go_template_value(
            self.runner.output(["docker", "inspect", "-f", "{{.State.Status}}", cid])
        )
        health = clean_go_template_value(
            self.runner.output(["docker", "inspect", "-f", "{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}", cid])
        )
        image = clean_go_template_value(
            self.runner.output(["docker", "inspect", "-f", "{{.Config.Image}}", cid])
        )
        label_version = clean_go_template_value(
            self.runner.output(["docker", "inspect", "-f", '{{index .Config.Labels "org.opencontainers.image.version"}}', cid])
        )
        revision = clean_go_template_value(
            self.runner.output(["docker", "inspect", "-f", '{{index .Config.Labels "org.opencontainers.image.revision"}}', cid])
        )
        env_lines = self.runner.output(["docker", "inspect", "-f", "{{range .Config.Env}}{{println .}}{{end}}", cid])
        env = parse_env_lines(env_lines.splitlines())
        version = (
            env.get("ARGUS_COMPONENT_VERSION")
            or env.get("ARGUS_ENGINE_VERSION")
            or label_version
            or image_tag(image)
        )

        update = False
        reasons: list[str] = []
        if latest.version and version and version != latest.version:
            update = True
            reasons.append(f"{version} -> {latest.version}")
        if latest.sha and revision and revision != latest.sha and not latest.sha.startswith(revision):
            update = True
            reasons.append(f"{short_sha(revision)} -> {short_sha(latest.sha)}")

        return ServiceStatus(
            service,
            True,
            state or "unknown",
            health or "none",
            version,
            revision,
            image,
            update,
            "; ".join(reasons) if reasons else "current",
        )

    def update_individual(self) -> int:
        latest = self.latest_release()
        statuses = [s for s in self.service_statuses(latest) if s.deployed]
        outdated = [s for s in statuses if s.update_available]
        candidates = outdated or statuses
        if not candidates:
            print_warn("No deployed components found.")
            pause()
            return 0

        print_table(
            ["#", "Component", "Deployed", "Latest", "Update"],
            [
                [str(i + 1), s.name, s.version or "-", latest.version or "-", "YES" if s.update_available else "no"]
                for i, s in enumerate(candidates)
            ],
        )
        selected = select_from_candidates([s.name for s in candidates], "Component(s) to update")
        if not selected:
            return 0
        return self.update_services(selected, latest)

    def update_all_available(self) -> int:
        latest = self.latest_release()
        outdated = [s.name for s in self.service_statuses(latest) if s.update_available]
        if not outdated:
            print_info("All deployed components appear current.")
            pause()
            return 0

        print_warn("Components with available updates:")
        for name in outdated:
            print(f"  - {name}")

        if not confirm("Update all components listed above?", default=True, assume_yes=self.assume_yes):
            return 0
        return self.update_services(outdated, latest)

    def update_services(self, services: Sequence[str], latest: LatestRelease | None = None) -> int:
        latest = latest or self.latest_release()
        env = os.environ.copy()
        env["ARGUS_NO_UI"] = "1"
        if latest.version:
            env["ARGUS_ENGINE_VERSION"] = latest.version
        if latest.sha:
            env["BUILD_SOURCE_STAMP"] = latest.sha

        if (self.root / ".git").exists():
            if confirm("Run git pull --ff-only before updating?", default=True, assume_yes=self.assume_yes):
                rc = self.runner.call(["git", "pull", "--ff-only"], env=env)
                if rc != 0:
                    return rc

        services = list(dict.fromkeys(services))
        if set(services) == set(self.compose_services()):
            return self.runner.call(["bash", str(self.deploy_dir / "deploy.sh"), "--image"], env=env)

        print_info("Building selected component image(s)...")
        rc = self.runner.call([*self.compose_cmd(), "build", *services], env=env)
        if rc != 0:
            return rc

        print_info("Recreating selected component container(s)...")
        return self.runner.call(
            [*self.compose_cmd(), "up", "-d", "--no-deps", "--force-recreate", *services],
            env=env,
        )

    def config_wizard(self) -> int:
        choice = choose(
            "One-time provider configuration",
            [
                "Configure all providers",
                "AWS only",
                "Azure only",
                "Google Cloud only",
                "Command Center/web control URL",
                "Show config files",
                "Back",
            ],
        )
        if choice == 0:
            for fn in [self.configure_aws, self.configure_azure, self.configure_gcp, self.configure_command_center]:
                rc = fn()
                if rc != 0:
                    return rc
        elif choice == 1:
            return self.configure_aws()
        elif choice == 2:
            return self.configure_azure()
        elif choice == 3:
            return self.configure_gcp()
        elif choice == 4:
            return self.configure_command_center()
        elif choice == 5:
            self.show_config_files()
            pause()
        return 0

    def configure_command_center(self) -> int:
        path = self.deploy_dir / ".env.local"
        env = read_env(path)
        url = prompt("Command Center URL used by scalers/web controls", env.get("COMMAND_CENTER_URL", "http://localhost:8081"))
        web = prompt("Command Center web URL", env.get("ARGUS_WEB_URL", "http://localhost:8082"))
        env["COMMAND_CENTER_URL"] = url
        env["ARGUS_WEB_URL"] = web
        write_env(path, env, "Argus local deployment UI configuration")
        print_info(f"Saved {path.relative_to(self.root)}")
        return 0

    def configure_aws(self) -> int:
        env_path = ensure_env_file(self.deploy_dir / "aws" / ".env", self.deploy_dir / "aws" / ".env.example")
        ensure_env_file(self.deploy_dir / "aws" / "service-env", self.deploy_dir / "aws" / "service-env.example")
        env = read_env(env_path)
        env["AWS_REGION"] = prompt("AWS region", env.get("AWS_REGION", "us-east-1"))
        env["AWS_ACCOUNT_ID"] = prompt("AWS account ID", env.get("AWS_ACCOUNT_ID", ""))
        env["ECS_CLUSTER"] = prompt("ECS cluster", env.get("ECS_CLUSTER", "argus-engine"))
        env["ECR_PREFIX"] = prompt("ECR repository prefix", env.get("ECR_PREFIX", "argus-engine"))
        env["COMMAND_CENTER_URL"] = prompt("Command Center URL reachable by autoscaler", env.get("COMMAND_CENTER_URL", "http://localhost:8081"))
        write_env(env_path, env, "AWS deployment settings for Argus Engine")
        if shutil.which("aws"):
            rc = self.runner.call(["aws", "sts", "get-caller-identity"], env=self.provider_env("aws"))
            if rc != 0 and confirm("Run aws configure now?", default=True, assume_yes=self.assume_yes):
                return self.runner.call(["aws", "configure"])
        else:
            print_warn("AWS CLI not found. Install/configure it before running AWS deployments.")
        return 0

    def configure_azure(self) -> int:
        env_path = ensure_env_file(self.deploy_dir / "azure" / ".env", self.deploy_dir / "azure" / ".env.example")
        ensure_env_file(self.deploy_dir / "azure" / "service-env", self.deploy_dir / "azure" / "service-env.example")
        env = read_env(env_path)
        env["AZURE_SUBSCRIPTION_ID"] = prompt("Azure subscription ID", env.get("AZURE_SUBSCRIPTION_ID", ""))
        env["AZURE_LOCATION"] = prompt("Azure location", env.get("AZURE_LOCATION", "eastus"))
        env["AZURE_RESOURCE_GROUP"] = prompt("Azure resource group", env.get("AZURE_RESOURCE_GROUP", "argus-engine-rg"))
        env["AZURE_CONTAINERAPPS_ENV"] = prompt("Azure Container Apps environment", env.get("AZURE_CONTAINERAPPS_ENV", "argus-engine-env"))
        env["AZURE_ACR_NAME"] = prompt("Azure ACR name", env.get("AZURE_ACR_NAME", ""))
        env["AZURE_IMAGE_PREFIX"] = prompt("Azure image prefix", env.get("AZURE_IMAGE_PREFIX", "argus-engine"))
        write_env(env_path, env, "Azure deployment settings for Argus Engine")
        if shutil.which("az"):
            rc = self.runner.call(["az", "account", "show", "--output", "table"], env=self.provider_env("azure"))
            if rc != 0 and confirm("Run az login now?", default=True, assume_yes=self.assume_yes):
                rc = self.runner.call(["az", "login"])
            if env.get("AZURE_SUBSCRIPTION_ID"):
                self.runner.call(["az", "account", "set", "--subscription", env["AZURE_SUBSCRIPTION_ID"]])
            return rc
        print_warn("Azure CLI not found. Install/configure it before running Azure deployments.")
        return 0

    def configure_gcp(self) -> int:
        env_path = ensure_env_file(self.deploy_dir / "gcp" / ".env", self.deploy_dir / "gcp" / ".env.example")
        ensure_env_file(self.deploy_dir / "gcp" / "service-env", self.deploy_dir / "gcp" / "service-env.example")
        env = read_env(env_path)
        env["GCP_PROJECT_ID"] = prompt("GCP project ID", env.get("GCP_PROJECT_ID", ""))
        env["GCP_REGION"] = prompt("GCP region", env.get("GCP_REGION", "us-central1"))
        env["GCP_ARTIFACT_REPOSITORY"] = prompt("Artifact Registry repository", env.get("GCP_ARTIFACT_REPOSITORY", "argus-engine"))
        env["GCP_IMAGE_PREFIX"] = prompt("GCP image prefix", env.get("GCP_IMAGE_PREFIX", "argus-engine"))
        write_env(env_path, env, "Google Cloud deployment settings for Argus Engine")
        if shutil.which("gcloud"):
            rc = self.runner.call(["gcloud", "auth", "list"], env=self.provider_env("gcp"))
            if rc != 0 and confirm("Run gcloud auth login now?", default=True, assume_yes=self.assume_yes):
                rc = self.runner.call(["gcloud", "auth", "login"])
            if env.get("GCP_PROJECT_ID"):
                self.runner.call(["gcloud", "config", "set", "project", env["GCP_PROJECT_ID"]])
            return rc
        print_warn("Google Cloud CLI not found. Install/configure it before running GCP deployments.")
        return 0

    def scale_provider(self, provider: str) -> int:
        services = self.select_worker_services()
        if not services:
            return 0

        if provider == "aws":
            env_path = ensure_env_file(self.deploy_dir / "aws" / ".env", self.deploy_dir / "aws" / ".env.example")
            env = read_env(env_path)
            for service in services:
                key = service_key(service)
                env[f"ECS_DESIRED_{key}"] = prompt(f"{service} desired ECS tasks", env.get(f"ECS_DESIRED_{key}", "1"))
                env[f"ECS_MIN_{key}"] = prompt(f"{service} min ECS tasks", env.get(f"ECS_MIN_{key}", "0"))
                env[f"ECS_MAX_{key}"] = prompt(f"{service} max ECS tasks", env.get(f"ECS_MAX_{key}", "20"))
            env["UPDATE_DESIRED_COUNTS"] = "true"
            write_env(env_path, env, "AWS deployment settings for Argus Engine")
            run_env = self.provider_env("aws")
            run_env["UPDATE_DESIRED_COUNTS"] = "true"
            return self.runner.call(["bash", str(self.script("aws", "deploy-ecs-services.sh")), *services], env=run_env)

        if provider == "azure":
            env_path = ensure_env_file(self.deploy_dir / "azure" / ".env", self.deploy_dir / "azure" / ".env.example")
            env = read_env(env_path)
            env["AZURE_MIN_REPLICAS"] = prompt("Azure min replicas", env.get("AZURE_MIN_REPLICAS", "0"))
            env["AZURE_MAX_REPLICAS"] = prompt("Azure max replicas", env.get("AZURE_MAX_REPLICAS", "3"))
            write_env(env_path, env, "Azure deployment settings for Argus Engine")
            return self.run_script("azure", "deploy-containerapps-workers.sh", services)

        if provider == "gcp":
            env_path = ensure_env_file(self.deploy_dir / "gcp" / ".env", self.deploy_dir / "gcp" / ".env.example")
            env = read_env(env_path)
            for service in services:
                key = service_key(service)
                env[f"GCP_WORKER_INSTANCES_{key}"] = prompt(
                    f"{service} Cloud Run worker-pool instances",
                    env.get(f"GCP_WORKER_INSTANCES_{key}", env.get("GCP_WORKER_INSTANCES", "1")),
                )
            write_env(env_path, env, "Google Cloud deployment settings for Argus Engine")
            return self.run_script("gcp", "deploy-cloudrun-worker-pools.sh", services)

        print_error(f"Unknown provider: {provider}")
        return 2

    def select_services(self, *, allow_all: bool = True, allow_empty: bool = False) -> list[str]:
        services = self.compose_services()
        return select_from_candidates(services, "Services", allow_all=allow_all, allow_empty=allow_empty)

    def select_worker_services(self) -> list[str]:
        return select_from_candidates(WORKER_SERVICES, "Worker services", allow_all=True)

    def select_cloud_services(self, provider: str) -> list[str]:
        if provider == "aws":
            return select_from_candidates(WORKER_SERVICES, "Cloud services", allow_all=True)
        return select_from_candidates(WORKER_SERVICES, "Cloud worker services", allow_all=True)

    def show_services(self) -> None:
        print_table(
            ["Service", "Type"],
            [[s, "worker" if s in WORKER_SERVICES else "core"] for s in self.compose_services()],
        )

    def show_config_files(self) -> None:
        for path in [
            self.deploy_dir / ".env.local",
            self.deploy_dir / "aws" / ".env",
            self.deploy_dir / "aws" / "service-env",
            self.deploy_dir / "azure" / ".env",
            self.deploy_dir / "azure" / "service-env",
            self.deploy_dir / "gcp" / ".env",
            self.deploy_dir / "gcp" / "service-env",
        ]:
            status = "exists" if path.exists() else "missing"
            print(f"{path.relative_to(self.root)}  [{status}]")

    def open_web_urls(self) -> None:
        env = read_env(self.deploy_dir / ".env.local")
        urls = [
            env.get("ARGUS_WEB_URL", "http://localhost:8082"),
            env.get("COMMAND_CENTER_URL", "http://localhost:8081"),
            "http://localhost:15672",
        ]
        for url in urls:
            print_info(f"Opening {url}")
            if not self.dry_run:
                webbrowser.open(url)

    def run_dotnet_ui(self, args: Sequence[str]) -> int:
        project = self.deploy_dir / "ArgusEngine.DeployUi" / "ArgusEngine.DeployUi.csproj"
        dotnet = shutil.which("dotnet")
        if not dotnet or not project.exists():
            print_error("dotnet or deploy/ArgusEngine.DeployUi was not found.")
            return 2
        return self.runner.call([dotnet, "run", "--project", str(project), "--", *args])


def resolve_repo_root(explicit: str | None = None) -> Path:
    if explicit:
        return Path(explicit).resolve()

    candidates = [Path.cwd(), Path(__file__).resolve().parent, Path(__file__).resolve().parent.parent]
    for start in candidates:
        for path in [start, *start.parents]:
            if (path / "ArgusEngine.slnx").exists() and (path / "deploy" / "docker-compose.yml").exists():
                return path.resolve()

    return Path(__file__).resolve().parent.parent


def parse_args(argv: Sequence[str]) -> tuple[list[str], bool, bool, str | None]:
    dry_run = False
    assume_yes = False
    root: str | None = None
    remaining: list[str] = []
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg in {"--dry-run", "-n"}:
            dry_run = True
        elif arg in {"--yes", "-y"}:
            assume_yes = True
        elif arg == "--root":
            i += 1
            if i >= len(argv):
                raise SystemExit("--root requires a path")
            root = argv[i]
        else:
            remaining.append(arg)
        i += 1
    return remaining, dry_run, assume_yes, root


def dispatch(app: ArgusDeployUi, args: Sequence[str]) -> int:
    if not args or args[0] in {"menu", "up"}:
        if sys.stdin.isatty():
            return app.main_menu()
        return app.deploy_script(["up"])

    command = args[0].lower()
    rest = list(args[1:])

    if command in {"--fresh", "-fresh"}:
        return app.deploy_script(["--fresh"])
    if command == "--ecs-workers":
        return app.deploy_script(["--ecs-workers"])
    if command in {"help", "-h", "--help"}:
        show_help()
        return 0
    if command == "dotnet-ui":
        return app.run_dotnet_ui(rest)
    if command == "status":
        return app.status_all()
    if command in {"versions", "updates"}:
        app.print_versions()
        return 0
    if command == "services":
        app.show_services()
        return 0
    if command == "update":
        if not rest or rest[0] == "all":
            return app.update_all_available()
        return app.update_services(rest)
    if command == "local":
        return app.deploy_script(rest or ["up"])
    if command == "aws":
        return dispatch_aws(app, rest)
    if command in {"azure", "az"}:
        return dispatch_azure(app, rest)
    if command in {"gcp", "google"}:
        return dispatch_gcp(app, rest)
    if command == "cloud":
        return dispatch_cloud(app, rest)
    if command == "config":
        if not rest or rest[0] == "wizard":
            return app.config_wizard()
        app.show_config_files()
        return 0
    if command == "scale":
        provider = rest[0] if rest else ""
        if provider in {"aws", "azure", "gcp"}:
            return app.scale_provider(provider)
        app.scale_menu()
        return 0
    if command == "open-web":
        app.open_web_urls()
        return 0

    print_error(f"Unknown command: {command}")
    show_help()
    return 2


def dispatch_aws(app: ArgusDeployUi, args: Sequence[str]) -> int:
    action = args[0].lower() if args else "status"
    services = args[1:]
    if action in {"login", "configure", "config"}:
        return app.configure_aws()
    if action == "hybrid":
        return app.deploy_script(["--ecs-workers"])
    if action in {"resources", "repos"}:
        return app.run_script("aws", "create-ecr-repos.sh")
    if action == "build":
        return app.run_script("aws", "build-push-ecr.sh", services or WORKER_SERVICES)
    if action == "deploy":
        return app.run_script("aws", "deploy-ecs-services.sh", services or WORKER_SERVICES)
    if action == "release":
        selected = services or WORKER_SERVICES
        if app.run_script("aws", "create-ecr-repos.sh") == 0:
            if app.run_script("aws", "build-push-ecr.sh", selected) == 0:
                return app.run_script("aws", "deploy-ecs-services.sh", selected)
        return 1
    if action == "replace":
        return app.run_script("aws", "replace-ecs-worker-tasks.sh", services or WORKER_SERVICES)
    if action == "autoscale":
        return app.run_script("aws", "autoscale-ecs-workers.sh")
    if action == "provision-ec2":
        return app.run_script("aws", "provision-ec2-workers.sh")
    if action == "deploy-ec2":
        return app.run_script("aws", "deploy-worker-instances.sh", services)
    if action == "scale":
        return app.scale_provider("aws")
    if action == "status":
        return app.aws_status()
    print_error(f"Unknown AWS action: {action}")
    return 2


def dispatch_azure(app: ArgusDeployUi, args: Sequence[str]) -> int:
    action = args[0].lower() if args else "status"
    services = args[1:]
    if action in {"login", "configure", "config"}:
        return app.configure_azure()
    if action in {"resources", "create"}:
        return app.run_script("azure", "create-containerapps-resources.sh")
    if action == "build":
        return app.run_script("azure", "build-push-acr.sh", services or WORKER_SERVICES)
    if action == "deploy":
        return app.run_script("azure", "deploy-containerapps-workers.sh", services or WORKER_SERVICES)
    if action == "release":
        selected = services or WORKER_SERVICES
        if app.run_script("azure", "create-containerapps-resources.sh") == 0:
            if app.run_script("azure", "build-push-acr.sh", selected) == 0:
                return app.run_script("azure", "deploy-containerapps-workers.sh", selected)
        return 1
    if action == "scale":
        return app.scale_provider("azure")
    if action == "status":
        return app.azure_status()
    print_error(f"Unknown Azure action: {action}")
    return 2


def dispatch_gcp(app: ArgusDeployUi, args: Sequence[str]) -> int:
    action = args[0].lower() if args else "status"
    services = args[1:]
    if action in {"login", "configure", "config"}:
        return app.configure_gcp()
    if action in {"repository", "resources", "create"}:
        return app.run_script("gcp", "create-artifact-registry.sh")
    if action == "build":
        return app.run_script("gcp", "build-push-artifact-registry.sh", services or WORKER_SERVICES)
    if action == "deploy":
        return app.run_script("gcp", "deploy-cloudrun-worker-pools.sh", services or WORKER_SERVICES)
    if action == "release":
        selected = services or WORKER_SERVICES
        if app.run_script("gcp", "create-artifact-registry.sh") == 0:
            if app.run_script("gcp", "build-push-artifact-registry.sh", selected) == 0:
                return app.run_script("gcp", "deploy-cloudrun-worker-pools.sh", selected)
        return 1
    if action == "scale":
        return app.scale_provider("gcp")
    if action == "status":
        return app.gcp_status()
    print_error(f"Unknown GCP action: {action}")
    return 2


def dispatch_cloud(app: ArgusDeployUi, args: Sequence[str]) -> int:
    action = args[0].lower() if args else "status"
    services = args[1:] or WORKER_SERVICES
    if action in {"login", "configure", "config"}:
        return app.config_wizard()
    if action == "status":
        return app.status_all()
    if action in {"release-all", "all", "release"}:
        for provider, fn in [
            ("aws", lambda: dispatch_aws(app, ["release", *services])),
            ("azure", lambda: dispatch_azure(app, ["release", *services])),
            ("gcp", lambda: dispatch_gcp(app, ["release", *services])),
        ]:
            print_title(f"Cloud release: {provider.upper()}")
            rc = fn()
            if rc != 0:
                return rc
        return 0
    if action == "scale":
        app.scale_menu()
        return 0
    print_error(f"Unknown cloud action: {action}")
    return 2


def show_help() -> None:
    print(
        textwrap.dedent(
            f"""
            Argus Engine deploy UI

            Interactive:
              python3 deploy/deploy-ui.py
              ./deploy/deploy.sh up

            Version/update:
              python3 deploy/deploy-ui.py versions
              python3 deploy/deploy-ui.py update all
              python3 deploy/deploy-ui.py update worker-spider command-center-web

            Local:
              python3 deploy/deploy-ui.py local --hot
              python3 deploy/deploy-ui.py local --image
              python3 deploy/deploy-ui.py local status
              python3 deploy/deploy-ui.py local logs worker-spider

            Cloud:
              python3 deploy/deploy-ui.py config wizard
              python3 deploy/deploy-ui.py aws hybrid
              python3 deploy/deploy-ui.py aws release worker-spider worker-enum
              python3 deploy/deploy-ui.py azure release worker-spider
              python3 deploy/deploy-ui.py gcp release worker-spider
              python3 deploy/deploy-ui.py cloud release-all

            Scaling:
              python3 deploy/deploy-ui.py scale aws
              python3 deploy/deploy-ui.py scale azure
              python3 deploy/deploy-ui.py scale gcp

            Global:
              --dry-run / -n
              --yes / -y
              --root <path>
            """
        ).strip()
    )


def fetch_text(url: str, timeout: int = 12) -> str:
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "argus-engine-deploy-ui/1.0",
            "Accept": "application/vnd.github+json,text/plain,*/*",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read().decode("utf-8", errors="replace")
    except (urllib.error.URLError, TimeoutError, OSError):
        return ""


def read_env(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    return parse_env_lines(path.read_text(encoding="utf-8", errors="ignore").splitlines())


def parse_env_lines(lines: Iterable[str]) -> dict[str, str]:
    env: dict[str, str] = {}
    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key:
            env[key] = value
    return env


def write_env(path: Path, values: dict[str, str], title: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    existing = path.read_text(encoding="utf-8", errors="ignore").splitlines() if path.exists() else []
    seen: set[str] = set()
    output: list[str] = []

    if not existing:
        output.extend([f"# {title}", "# Generated by deploy/deploy-ui.py. Do not commit secrets.", ""])

    for line in existing:
        stripped = line.strip()
        if not stripped or stripped.startswith("#") or "=" not in stripped:
            output.append(line)
            continue
        key = stripped.split("=", 1)[0].strip()
        if key in values:
            output.append(f"{key}={shell_env_quote(values[key])}")
            seen.add(key)
        else:
            output.append(line)

    for key in sorted(values):
        if key not in seen:
            output.append(f"{key}={shell_env_quote(values[key])}")

    path.write_text("\n".join(output).rstrip() + "\n", encoding="utf-8")


def ensure_env_file(path: Path, example: Path) -> Path:
    if path.exists():
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    if example.exists():
        path.write_text(example.read_text(encoding="utf-8", errors="ignore"), encoding="utf-8")
    else:
        path.write_text("# Generated by deploy/deploy-ui.py\n", encoding="utf-8")
    return path


def shell_env_quote(value: str) -> str:
    if value == "":
        return ""
    if re.fullmatch(r"[A-Za-z0-9_./:@,+=-]+", value):
        return value
    return shlex.quote(value)


def choose(title: str, options: Sequence[str]) -> int:
    print_title(title)
    for i, option in enumerate(options, 1):
        print(f"  [{i}] {option}")
    while True:
        raw = input("Select: ").strip()
        if not raw:
            continue
        if raw.isdigit() and 1 <= int(raw) <= len(options):
            return int(raw) - 1
        print_warn(f"Enter a number from 1 to {len(options)}.")


def select_from_candidates(
    candidates: Sequence[str],
    label: str,
    *,
    allow_all: bool = True,
    allow_empty: bool = False,
) -> list[str]:
    candidates = list(candidates)
    if not candidates:
        return []

    print_title(label)
    for i, value in enumerate(candidates, 1):
        print(f"  [{i:2}] {value}")

    hints = []
    if allow_all:
        hints.append("all")
    if allow_empty:
        hints.append("none")
    hint = f" ({', '.join(hints)} allowed)" if hints else ""
    raw = prompt(f"Select by number/name, comma or space separated{hint}", "all" if allow_all else "")

    if not raw and allow_empty:
        return []
    if raw.lower() == "none" and allow_empty:
        return []
    if raw.lower() == "all" and allow_all:
        return candidates

    selected: list[str] = []
    tokens = [t for t in re.split(r"[,\s]+", raw.strip()) if t]
    lookup = {name.lower(): name for name in candidates}
    for token in tokens:
        if token.isdigit():
            idx = int(token) - 1
            if 0 <= idx < len(candidates):
                selected.append(candidates[idx])
                continue
        match = lookup.get(token.lower())
        if match:
            selected.append(match)
        else:
            print_warn(f"Ignoring unknown selection: {token}")

    return list(dict.fromkeys(selected))


def prompt(label: str, default: str = "") -> str:
    suffix = f" [{default}]" if default else ""
    value = input(f"{label}{suffix}: ").strip()
    return value if value else default


def confirm(label: str, *, default: bool = True, assume_yes: bool = False) -> bool:
    if assume_yes:
        return True
    suffix = "Y/n" if default else "y/N"
    value = input(f"{label} [{suffix}]: ").strip().lower()
    if not value:
        return default
    return value in {"y", "yes", "true", "1"}


def print_cmd(args: Sequence[str]) -> None:
    print(f"$ {shlex.join(str(a) for a in args)}")


def print_title(title: str) -> None:
    print()
    print(f"== {title} ==")


def print_info(message: str) -> None:
    print(f"[info] {message}")


def print_warn(message: str) -> None:
    print(f"[warn] {message}")


def print_error(message: str) -> None:
    print(f"[error] {message}", file=sys.stderr)


def print_box(title: str, lines: Sequence[str]) -> None:
    width = max([len(title), *(len(line) for line in lines)], default=len(title)) + 4
    print("┌" + "─" * width + "┐")
    print("│ " + title.ljust(width - 2) + " │")
    print("├" + "─" * width + "┤")
    for line in lines:
        print("│ " + line.ljust(width - 2) + " │")
    print("└" + "─" * width + "┘")


def print_table(headers: Sequence[str], rows: Sequence[Sequence[str]]) -> None:
    data = [list(map(str, headers)), *[list(map(str, row)) for row in rows]]
    widths = [max(len(row[i]) for row in data) for i in range(len(headers))]
    fmt = "  ".join("{:<" + str(width) + "}" for width in widths)
    print(fmt.format(*headers))
    print(fmt.format(*["-" * width for width in widths]))
    for row in rows:
        print(fmt.format(*map(str, row)))


def pause() -> None:
    if sys.stdin.isatty():
        input("\nPress Enter to continue...")


def clear_screen() -> None:
    if sys.stdout.isatty():
        os.system("cls" if os.name == "nt" else "clear")


def first_line(value: str) -> str:
    return value.splitlines()[0].strip() if value.splitlines() else ""


def clean_go_template_value(value: str) -> str:
    value = value.strip()
    if value in {"<no value>", "null", "None"}:
        return ""
    return value


def image_tag(image: str) -> str:
    last = image.rsplit("/", 1)[-1]
    if ":" in last:
        return last.rsplit(":", 1)[-1]
    return ""


def short_sha(value: str) -> str:
    return value[:12] if value else "unknown"


def service_key(service: str) -> str:
    key = service.upper().replace("-", "_")
    if key.startswith("WORKER_"):
        return key
    return key


def first_nonzero(*codes: int) -> int:
    for code in codes:
        if code:
            return code
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    argv = list(argv if argv is not None else sys.argv[1:])
    args, dry_run, assume_yes, explicit_root = parse_args(argv)
    root = resolve_repo_root(explicit_root)
    app = ArgusDeployUi(root, dry_run=dry_run, assume_yes=assume_yes)
    return dispatch(app, args)


if __name__ == "__main__":
    raise SystemExit(main())
'''

def main() -> None:
    out = Path("argus-engine-deploy-ui-changes.zip").resolve()
    info = ZipInfo("deploy/deploy-ui.py")
    info.external_attr = 0o755 << 16

    with ZipFile(out, "w", compression=ZIP_DEFLATED) as zf:
        zf.writestr(info, DEPLOY_UI)

    print(f"Wrote {out}")
    print("Zip contents:")
    print("  deploy/deploy-ui.py")

if __name__ == "__main__":
    main()