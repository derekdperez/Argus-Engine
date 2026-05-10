#!/usr/bin/env python3
from __future__ import annotations

import textwrap
import zipfile
from pathlib import Path

OUT = Path("argus-engine-deployops-patch.zip")

FILES: dict[str, str] = {
    "deploy/deploy-ui.py": r'''#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable, Optional


APP_NAME = "Argus Engine DeployOps"

SECRET_KEYWORDS = (
    "PASSWORD",
    "SECRET",
    "TOKEN",
    "KEY",
    "CONNECTIONSTRINGS",
    "CONNECTION_STRING",
    "PRIVATE",
    "CREDENTIAL",
)

DEFAULT_PORTS = {
    5432: "PostgreSQL",
    6379: "Redis",
    5672: "RabbitMQ AMQP",
    15672: "RabbitMQ Management",
    8081: "Command Center Gateway",
    8082: "Command Center Web",
    8083: "Operations API",
    8084: "Discovery API",
    8085: "Worker Control API",
    8086: "Maintenance API",
    8087: "Updates API",
    8088: "Realtime Host",
}

ARGUS_SERVICES = [
    "postgres",
    "filestore-db-init",
    "redis",
    "rabbitmq",
    "command-center-gateway",
    "command-center-web",
    "command-center-operations-api",
    "command-center-discovery-api",
    "command-center-worker-control-api",
    "command-center-maintenance-api",
    "command-center-updates-api",
    "command-center-realtime",
    "command-center-bootstrapper",
    "command-center-spider-dispatcher",
    "gatekeeper",
    "worker-spider",
    "worker-http-requester",
    "worker-enum",
    "worker-portscan",
    "worker-highvalue",
    "worker-techid",
]

LOCAL_REQUIRED_ENV = [
    "ARGUS_ENGINE_VERSION",
    "ARGUS_DIAGNOSTICS_API_KEY",
]

AWS_REQUIRED_ENV = [
    "AWS_REGION",
    "AWS_ACCOUNT_ID",
    "ECS_CLUSTER",
    "ECS_SUBNETS",
    "ECS_SECURITY_GROUPS",
    "ECS_TASK_EXECUTION_ROLE_ARN",
]

ERROR_PATTERN = re.compile(
    r"(^|[^a-z])(fail|failed|fatal|panic|exception|error|critical|unhandled|"
    r"404|status=404|OptionsValidationException)([^a-z]|$)",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class RepoContext:
    root: Path
    deploy_dir: Path
    compose_file: Path
    deploy_sh: Path
    logs_sh: Path
    smoke_test_sh: Path
    dev_check_sh: Path
    aws_dir: Path

    @staticmethod
    def discover() -> "RepoContext":
        deploy_dir = Path(__file__).resolve().parent
        root = deploy_dir.parent

        if not (root / "ArgusEngine.slnx").exists():
            cursor = Path.cwd().resolve()
            for candidate in [cursor, *cursor.parents]:
                if (candidate / "ArgusEngine.slnx").exists() and (candidate / "deploy").is_dir():
                    root = candidate
                    deploy_dir = root / "deploy"
                    break

        return RepoContext(
            root=root,
            deploy_dir=deploy_dir,
            compose_file=deploy_dir / "docker-compose.yml",
            deploy_sh=deploy_dir / "deploy.sh",
            logs_sh=deploy_dir / "logs.sh",
            smoke_test_sh=deploy_dir / "smoke-test.sh",
            dev_check_sh=deploy_dir / "dev-check.sh",
            aws_dir=deploy_dir / "aws",
        )


@dataclass
class Runtime:
    ctx: RepoContext
    dry_run: bool = False
    verbose: bool = False
    yes: bool = False
    log_file: Optional[Path] = None

    def __post_init__(self) -> None:
        if self.log_file is None:
            log_dir = self.ctx.deploy_dir / "logs"
            log_dir.mkdir(parents=True, exist_ok=True)
            stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
            self.log_file = log_dir / f"deployops_{stamp}.log"

    def log(self, message: str) -> None:
        safe = redact(message)
        assert self.log_file is not None
        with self.log_file.open("a", encoding="utf-8") as fh:
            fh.write(safe)
            if not safe.endswith("\n"):
                fh.write("\n")

    def run(
        self,
        args: list[str],
        *,
        env: Optional[dict[str, str]] = None,
        check: bool = True,
        stream: bool = True,
    ) -> subprocess.CompletedProcess[str]:
        rendered = " ".join(shell_quote(x) for x in args)
        self.log(f"$ {rendered}")

        if self.dry_run:
            print_warn(f"DRY RUN: {rendered}")
            return subprocess.CompletedProcess(args=args, returncode=0, stdout="", stderr="")

        merged_env = os.environ.copy()
        if env:
            merged_env.update(env)

        if self.verbose:
            print_dim(f"Running: {rendered}")

        if stream:
            process = subprocess.Popen(
                args,
                cwd=str(self.ctx.root),
                env=merged_env,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
            )
            assert process.stdout is not None

            captured: list[str] = []
            for line in process.stdout:
                captured.append(line)
                print(redact(line), end="")

            code = process.wait()
            output = "".join(captured)
            self.log(output)

            if check and code != 0:
                raise subprocess.CalledProcessError(code, args, output=output)

            return subprocess.CompletedProcess(args=args, returncode=code, stdout=output, stderr="")

        completed = subprocess.run(
            args,
            cwd=str(self.ctx.root),
            env=merged_env,
            text=True,
            capture_output=True,
            check=False,
        )
        self.log(completed.stdout)
        self.log(completed.stderr)

        if check and completed.returncode != 0:
            raise subprocess.CalledProcessError(
                completed.returncode,
                args,
                output=completed.stdout,
                stderr=completed.stderr,
            )

        return completed


def supports_color() -> bool:
    return sys.stdout.isatty() and os.environ.get("NO_COLOR") is None


def color(text: str, code: str) -> str:
    if not supports_color():
        return text
    return f"\033[{code}m{text}\033[0m"


def print_ok(text: str) -> None:
    print(color(text, "32"))


def print_warn(text: str) -> None:
    print(color(text, "33"))


def print_error(text: str) -> None:
    print(color(text, "31"), file=sys.stderr)


def print_dim(text: str) -> None:
    print(color(text, "2"))


def print_title(text: str) -> None:
    line = "=" * len(text)
    print()
    print(color(line, "36"))
    print(color(text, "36;1"))
    print(color(line, "36"))


def redact(value: str) -> str:
    lines = value.splitlines(keepends=True)
    redacted: list[str] = []

    for line in lines:
        stripped = line.strip()
        if "=" in stripped:
            left, right = stripped.split("=", 1)
            if any(k in left.upper() for k in SECRET_KEYWORDS):
                redacted.append(line.replace(right, "***", 1))
                continue
        redacted.append(line)

    return "".join(redacted)


def shell_quote(value: str) -> str:
    if not value:
        return "''"
    safe = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_+-=./:@")
    if all(ch in safe for ch in value):
        return value
    return "'" + value.replace("'", "'\"'\"'") + "'"


def command_exists(name: str) -> bool:
    return shutil.which(name) is not None


def port_in_use(port: int, host: str = "127.0.0.1") -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.25)
        return sock.connect_ex((host, port)) == 0


def parse_env_file(path: Path) -> dict[str, str]:
    data: dict[str, str] = {}
    if not path.exists():
        return data

    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip()] = value.strip().strip("'").strip('"')
    return data


def ensure_file(path: Path, label: str) -> None:
    if not path.exists():
        raise FileNotFoundError(f"{label} not found: {path}")


def prompt(text: str, default: str = "") -> str:
    suffix = f" [{default}]" if default else ""
    value = input(f"{text}{suffix}: ").strip()
    return value or default


def confirm(text: str, rt: Runtime, *, default: bool = False) -> bool:
    if rt.yes:
        return True

    marker = "Y/n" if default else "y/N"
    value = input(f"{text} [{marker}]: ").strip().lower()
    if not value:
        return default
    return value in {"y", "yes"}


def typed_confirm(text: str, expected: str, rt: Runtime) -> bool:
    if rt.yes:
        return True

    print_warn(text)
    value = input(f"Type {expected!r} to continue: ").strip()
    return value == expected


def choose(title: str, options: list[str]) -> Optional[int]:
    print_title(title)
    for i, item in enumerate(options, start=1):
        print(f"[{i}] {item}")
    print("[0] Back/Exit")

    while True:
        raw = input("Choose: ").strip()
        if raw in {"", "0"}:
            return None
        if raw.isdigit() and 1 <= int(raw) <= len(options):
            return int(raw) - 1
        print_warn("Invalid selection.")


def show_header(rt: Runtime) -> None:
    ctx = rt.ctx
    print_title(APP_NAME)
    print(f"repo:     {ctx.root}")
    print(f"compose:  {ctx.compose_file}")
    print(f"dry-run:  {rt.dry_run}")
    print(f"verbose:  {rt.verbose}")
    print(f"log file: {rt.log_file}")


def run_deploy_sh(rt: Runtime, args: Iterable[str], extra_env: Optional[dict[str, str]] = None) -> None:
    ensure_file(rt.ctx.deploy_sh, "deploy.sh")
    env = {"ARGUS_NO_UI": "1"}
    if extra_env:
        env.update(extra_env)
    rt.run(["bash", str(rt.ctx.deploy_sh), *args], env=env)


def preflight(rt: Runtime) -> None:
    show_header(rt)

    print_title("Command checks")
    for cmd in ["bash", "git", "docker", "dotnet", "curl", "aws", "az", "gcloud", "kubectl"]:
        path = shutil.which(cmd)
        status = "OK" if path else "missing"
        print(f"{cmd:10} {status:8} {path or ''}")

    print_title("Repository files")
    checks = [
        ("solution", rt.ctx.root / "ArgusEngine.slnx"),
        ("compose", rt.ctx.compose_file),
        ("deploy.sh", rt.ctx.deploy_sh),
        ("logs.sh", rt.ctx.logs_sh),
        ("smoke-test.sh", rt.ctx.smoke_test_sh),
        ("dev-check.sh", rt.ctx.dev_check_sh),
        ("aws dir", rt.ctx.aws_dir),
    ]
    for label, path in checks:
        print(f"{label:16} {'OK' if path.exists() else 'missing':8} {path}")

    print_title("Docker Compose")
    if command_exists("docker"):
        rt.run(["docker", "compose", "version"], check=False, stream=True)
    elif command_exists("docker-compose"):
        rt.run(["docker-compose", "--version"], check=False, stream=True)
    else:
        print_warn("Docker was not found.")

    print_title("Port conflicts")
    for port, label in DEFAULT_PORTS.items():
        busy = port_in_use(port)
        status = "IN USE" if busy else "free"
        print(f"{port:<6} {status:<8} {label}")

    print_title("Host resources")
    usage = shutil.disk_usage(str(rt.ctx.root))
    print(f"disk total: {usage.total // (1024**3)} GiB")
    print(f"disk used:  {usage.used // (1024**3)} GiB")
    print(f"disk free:  {usage.free // (1024**3)} GiB")

    if hasattr(os, "getloadavg"):
        load = os.getloadavg()
        print(f"load avg:   {load[0]:.2f}, {load[1]:.2f}, {load[2]:.2f}")

    meminfo = Path("/proc/meminfo")
    if meminfo.exists():
        mem = {}
        for line in meminfo.read_text(encoding="utf-8", errors="ignore").splitlines():
            if ":" in line:
                k, v = line.split(":", 1)
                mem[k] = v.strip()
        print(f"mem total:  {mem.get('MemTotal', 'unknown')}")
        print(f"mem avail:  {mem.get('MemAvailable', 'unknown')}")


def deploy(rt: Runtime, mode: str, no_smoke: bool = False) -> None:
    if mode not in {"hot", "image", "fresh", "ecs-workers"}:
        raise ValueError("mode must be one of: hot, image, fresh, ecs-workers")

    args = {
        "hot": ["--hot", "up"],
        "image": ["--image", "up"],
        "fresh": ["-fresh"],
        "ecs-workers": ["--ecs-workers"],
    }[mode]

    run_deploy_sh(rt, args)

    if not no_smoke and mode != "ecs-workers":
        smoke(rt)


def update(rt: Runtime, mode: str, pull: bool = True, no_smoke: bool = False) -> None:
    if pull:
        dirty = rt.run(["git", "status", "--porcelain"], check=False, stream=False).stdout.strip()
        if dirty:
            print_warn("Working tree has uncommitted changes.")
            print(dirty)
            if not confirm("Continue with git pull/deploy anyway?", rt):
                return
        rt.run(["git", "pull", "--ff-only"])
    deploy(rt, mode=mode, no_smoke=no_smoke)


def status(rt: Runtime, services: list[str]) -> None:
    run_deploy_sh(rt, ["status", *services])


def logs(rt: Runtime, services: list[str], tail: int, follow: bool, errors: bool) -> None:
    ensure_file(rt.ctx.logs_sh, "logs.sh")
    args = ["bash", str(rt.ctx.logs_sh), "--tail", str(tail)]
    if follow:
        args.append("--follow")
    if errors:
        args.append("--errors")
    args.extend(services)
    rt.run(args)


def restart(rt: Runtime, services: list[str]) -> None:
    run_deploy_sh(rt, ["restart", *services])


def stop(rt: Runtime) -> None:
    if confirm("Stop Argus Engine services?", rt):
        run_deploy_sh(rt, ["down"])


def start(rt: Runtime) -> None:
    run_deploy_sh(rt, ["up"])


def clean(rt: Runtime) -> None:
    if typed_confirm(
        "This removes Compose containers, orphans, volumes, and hot-publish output.",
        "delete volumes",
        rt,
    ):
        run_deploy_sh(rt, ["clean"], extra_env={"CONFIRM_ARGUS_CLEAN": "yes"})


def smoke(rt: Runtime) -> None:
    ensure_file(rt.ctx.smoke_test_sh, "smoke-test.sh")
    rt.run(["bash", str(rt.ctx.smoke_test_sh)])


def dev_check(rt: Runtime, fresh: bool = False, no_build: bool = False) -> None:
    ensure_file(rt.ctx.dev_check_sh, "dev-check.sh")
    args = ["bash", str(rt.ctx.dev_check_sh)]
    if fresh:
        args.append("--fresh")
    if no_build:
        args.append("--no-build")
    rt.run(args)


def health(rt: Runtime, base_url: str = "http://localhost:8081") -> None:
    print_title("HTTP health checks")
    paths = ["/health", "/health/ready", "/api/diagnostics/self", "/api/diagnostics/dependencies"]
    key = os.environ.get("ARGUS_DIAGNOSTICS_API_KEY") or os.environ.get("NIGHTMARE_DIAGNOSTICS_API_KEY") or "local-dev-diagnostics-key-change-me"

    for path in paths:
        url = base_url.rstrip("/") + path
        req = urllib.request.Request(
            url,
            headers={
                "X-Argus-Diagnostics-Key": key,
                "X-Nightmare-Diagnostics-Key": key,
            },
        )
        if rt.dry_run:
            print_warn(f"DRY RUN: GET {url}")
            continue

        try:
            started = time.time()
            with urllib.request.urlopen(req, timeout=10) as response:
                elapsed = int((time.time() - started) * 1000)
                print_ok(f"OK   {response.status:<3} {elapsed:>5}ms {url}")
        except urllib.error.HTTPError as exc:
            print_error(f"FAIL {exc.code:<3}       {url}")
        except Exception as exc:
            print_error(f"FAIL {'ERR':<3}       {url} ({exc})")


def init_config(rt: Runtime, profile: str) -> None:
    template = rt.ctx.deploy_dir / "config" / f"argus.{profile}.env.example"
    target = rt.ctx.deploy_dir / f".env.{profile}"

    ensure_file(template, f"{profile} config template")

    if target.exists() and not confirm(f"{target} exists. Overwrite?", rt):
        return

    if rt.dry_run:
        print_warn(f"DRY RUN: copy {template} -> {target}")
        return

    shutil.copy2(template, target)
    print_ok(f"Created {target}")
    print_warn("Review the file and replace placeholder secrets before production use.")


def validate_config(rt: Runtime, profile: str = "local") -> None:
    print_title(f"Config validation: {profile}")

    env_files = [
        rt.ctx.deploy_dir / f".env.{profile}",
        rt.ctx.deploy_dir / ".env",
    ]

    env: dict[str, str] = {}
    for path in env_files:
        env.update(parse_env_file(path))

    env.update({k: v for k, v in os.environ.items() if k.startswith("ARGUS_")})

    missing = [key for key in LOCAL_REQUIRED_ENV if not env.get(key)]

    if missing:
        print_warn("Missing local deployment values:")
        for key in missing:
            print(f"  - {key}")
    else:
        print_ok("Local deployment variables look present.")

    aws_env = parse_env_file(rt.ctx.aws_dir / ".env")
    aws_missing = [key for key in AWS_REQUIRED_ENV if not aws_env.get(key)]

    if (rt.ctx.aws_dir / ".env").exists():
        if aws_missing:
            print_warn("Missing AWS deployment values:")
            for key in aws_missing:
                print(f"  - {key}")
        else:
            print_ok("AWS deployment variables look present.")
    else:
        print_warn("deploy/aws/.env not found. AWS menu actions may require it.")


def sanitized_config_summary(rt: Runtime, profile: str = "local") -> None:
    print_title(f"Sanitized config summary: {profile}")
    paths = [
        rt.ctx.deploy_dir / f".env.{profile}",
        rt.ctx.deploy_dir / ".env",
        rt.ctx.aws_dir / ".env",
        rt.ctx.aws_dir / "service-env",
        rt.ctx.aws_dir / ".env.generated",
    ]

    for path in paths:
        if not path.exists():
            continue

        print(f"\n# {path.relative_to(rt.ctx.root)}")
        data = parse_env_file(path)
        for key in sorted(data):
            if any(s in key.upper() for s in SECRET_KEYWORDS):
                print(f"{key}=***")
            else:
                print(f"{key}={data[key]}")


def backup_config(rt: Runtime) -> None:
    stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    backup_root = rt.ctx.deploy_dir / "backups" / stamp

    files = [
        rt.ctx.deploy_dir / ".env",
        rt.ctx.deploy_dir / ".env.local",
        rt.ctx.deploy_dir / ".env.dev",
        rt.ctx.deploy_dir / ".env.staging",
        rt.ctx.deploy_dir / ".env.production",
        rt.ctx.aws_dir / ".env",
        rt.ctx.aws_dir / ".env.generated",
        rt.ctx.aws_dir / "service-env",
    ]

    manifest = {
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "files": [],
    }

    if rt.dry_run:
        print_warn(f"DRY RUN: mkdir -p {backup_root}")
    else:
        backup_root.mkdir(parents=True, exist_ok=True)

    for src in files:
        if not src.exists():
            continue
        dst = backup_root / src.relative_to(rt.ctx.root)
        manifest["files"].append(str(src.relative_to(rt.ctx.root)))

        if rt.dry_run:
            print_warn(f"DRY RUN: copy {src} -> {dst}")
        else:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)

    if not rt.dry_run:
        (backup_root / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print_ok(f"Config backup complete: {backup_root}")


def backup_postgres(rt: Runtime) -> None:
    stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    out = rt.ctx.deploy_dir / "backups" / stamp / "argus_engine.pg_dump"

    if not confirm("Run pg_dump from the postgres Compose service?", rt):
        return

    if rt.dry_run:
        print_warn(f"DRY RUN: docker compose exec postgres pg_dump ... > {out}")
        return

    out.parent.mkdir(parents=True, exist_ok=True)
    compose = ["docker", "compose", "-f", str(rt.ctx.compose_file)]
    cmd = compose + [
        "exec",
        "-T",
        "postgres",
        "pg_dump",
        "-U",
        "argus",
        "-d",
        "argus_engine",
        "-Fc",
    ]

    with out.open("wb") as fh:
        proc = subprocess.run(cmd, cwd=str(rt.ctx.root), stdout=fh, stderr=subprocess.PIPE, check=False)

    if proc.returncode != 0:
        print_error(proc.stderr.decode("utf-8", errors="replace"))
        raise subprocess.CalledProcessError(proc.returncode, cmd)

    print_ok(f"Postgres backup written: {out}")


def rollback_guidance(rt: Runtime) -> None:
    print_title("Rollback guidance")
    print(
        """
Recommended local rollback:

  1. Identify a known-good commit:
       git log --oneline -20

  2. Back up current config:
       python3 deploy/deploy-ui.py config backup

  3. Stop the stack:
       ARGUS_NO_UI=1 ./deploy/deploy.sh down

  4. Check out the known-good commit:
       git checkout <sha>

  5. Rebuild and redeploy:
       ARGUS_NO_UI=1 ./deploy/deploy.sh --image up

  6. Validate:
       ./deploy/smoke-test.sh
       ./deploy/logs.sh --errors

Recommended ECS rollback:

  - Prefer immutable image tags.
  - Roll back to a previous ECS task definition revision.
  - Avoid rebuilding and repushing a mutable latest tag during incident response.
""".strip()
    )


def aws_ecs_workers(rt: Runtime) -> None:
    if not command_exists("aws"):
        print_warn("AWS CLI was not found. deploy.sh may attempt installation only in supported Linux paths.")
    if confirm("Deploy EC2-hosted core stack with ECS workers?", rt):
        run_deploy_sh(rt, ["--ecs-workers"])


def aws_ecr_push(rt: Runtime, services: list[str]) -> None:
    ensure_file(rt.ctx.aws_dir / "create-ecr-repos.sh", "create-ecr-repos.sh")
    ensure_file(rt.ctx.aws_dir / "build-push-ecr.sh", "build-push-ecr.sh")
    rt.run(["bash", str(rt.ctx.aws_dir / "create-ecr-repos.sh")])
    rt.run(["bash", str(rt.ctx.aws_dir / "build-push-ecr.sh"), *services])


def aws_deploy_services(rt: Runtime, services: list[str]) -> None:
    ensure_file(rt.ctx.aws_dir / "deploy-ecs-services.sh", "deploy-ecs-services.sh")
    rt.run(["bash", str(rt.ctx.aws_dir / "deploy-ecs-services.sh"), *services])


def aws_autoscale(rt: Runtime) -> None:
    ensure_file(rt.ctx.aws_dir / "autoscale-ecs-workers.sh", "autoscale-ecs-workers.sh")
    rt.run(["bash", str(rt.ctx.aws_dir / "autoscale-ecs-workers.sh")])


def aws_status(rt: Runtime) -> None:
    script = rt.ctx.aws_dir / "ecs-command-center-status.sh"
    if script.exists():
        rt.run(["bash", str(script)], check=False)
        return

    aws_env = parse_env_file(rt.ctx.aws_dir / ".env")
    cluster = aws_env.get("ECS_CLUSTER") or os.environ.get("ECS_CLUSTER")
    if cluster:
        rt.run(["aws", "ecs", "describe-clusters", "--clusters", cluster], check=False)
        rt.run(["aws", "ecs", "list-services", "--cluster", cluster], check=False)
    else:
        print_warn("No ECS_CLUSTER found in deploy/aws/.env or environment.")


def aws_destroy_workers(rt: Runtime) -> None:
    ensure_file(rt.ctx.aws_dir / "destroy-ecs-services.sh", "destroy-ecs-services.sh")
    if typed_confirm("Delete ECS worker services only. This does not delete ECR/log groups/databases.", "destroy workers", rt):
        rt.run(
            ["bash", str(rt.ctx.aws_dir / "destroy-ecs-services.sh"), "workers"],
            env={"CONFIRM_DESTROY_ECS_WORKERS": "yes"},
        )


def cloud_plan(provider: str) -> None:
    print_title(f"{provider} deployment plan")
    if provider == "Azure":
        print(
            """
No Azure deployment scripts are currently included in this repository.

Recommended first path:
  - Azure VM + Docker Compose parity with EC2/local deploy.
  - Azure Managed Disk snapshots for backups.
  - Azure Monitor agent for host/container logs.
  - Azure Key Vault once config profiles are standardized.

Recommended later path:
  - Azure Container Registry for images.
  - Azure Container Apps for stateless APIs/workers where compatible.
  - Azure Database for PostgreSQL Flexible Server.
  - Azure Cache for Redis.
  - RabbitMQ remains containerized or must be replaced only after compatibility testing.
""".strip()
        )
    elif provider == "Google Cloud":
        print(
            """
No GCP deployment scripts are currently included in this repository.

Recommended first path:
  - Compute Engine + Docker Compose parity with EC2/local deploy.
  - Persistent disk snapshots for backups.
  - Cloud Logging agent for logs.

Recommended later path:
  - Artifact Registry for images.
  - Cloud Run for stateless APIs/workers where compatible.
  - Cloud SQL for PostgreSQL.
  - Memorystore for Redis.
  - RabbitMQ remains on Compute Engine or moves to GKE.
""".strip()
        )
    elif provider == "Kubernetes":
        print(
            """
No Kubernetes or Helm manifests are currently included in this repository.

Recommended path:
  - Keep Docker Compose as the operational baseline first.
  - Add deploy/helm/argus-engine with values for local/dev/staging/prod.
  - Model Postgres/RabbitMQ as external dependencies for production.
  - Use StatefulSets only for development or single-cluster self-hosted installs.
  - Add readiness/liveness probes matching /health and /health/ready.
""".strip()
        )


def local_developer_setup(rt: Runtime) -> None:
    if command_exists("dotnet"):
        rt.run(["dotnet", "restore", str(rt.ctx.root / "ArgusEngine.slnx")])
        rt.run(["dotnet", "build", str(rt.ctx.root / "ArgusEngine.slnx"), "--no-restore"])
    else:
        print_warn("dotnet was not found.")

    test_sh = rt.ctx.root / "test.sh"
    if test_sh.exists() and confirm("Run ./test.sh unit?", rt, default=False):
        rt.run(["bash", str(test_sh), "unit"])


def menu_initial(rt: Runtime) -> None:
    while True:
        idx = choose(
            "Initial deployment",
            [
                "Preflight checks",
                "Local Docker Compose deploy",
                "Fresh rebuild",
                "Local developer restore/build",
                "Dev check",
                "Smoke test",
            ],
        )
        if idx is None:
            return
        if idx == 0:
            preflight(rt)
        elif idx == 1:
            deploy(rt, "hot")
        elif idx == 2:
            deploy(rt, "fresh")
        elif idx == 3:
            local_developer_setup(rt)
        elif idx == 4:
            dev_check(rt)
        elif idx == 5:
            smoke(rt)


def menu_update(rt: Runtime) -> None:
    idx = choose("Update Argus Engine", ["Hot update", "Image update", "Fresh rebuild", "Pull only"])
    if idx is None:
        return
    if idx == 0:
        update(rt, "hot")
    elif idx == 1:
        update(rt, "image")
    elif idx == 2:
        update(rt, "fresh")
    elif idx == 3:
        rt.run(["git", "pull", "--ff-only"])


def menu_monitor(rt: Runtime) -> None:
    while True:
        idx = choose(
            "Monitoring and logs",
            [
                "Docker Compose status",
                "Logs all services",
                "Follow logs for selected service",
                "Error-only logs",
                "Smoke test",
                "HTTP health checks",
                "Host preflight/resource checks",
            ],
        )
        if idx is None:
            return
        if idx == 0:
            status(rt, [])
        elif idx == 1:
            logs(rt, [], 160, False, False)
        elif idx == 2:
            service = choose_service()
            if service:
                logs(rt, [service], 160, True, False)
        elif idx == 3:
            logs(rt, [], 300, False, True)
        elif idx == 4:
            smoke(rt)
        elif idx == 5:
            base_url = prompt("Base URL", "http://localhost:8081")
            health(rt, base_url)
        elif idx == 6:
            preflight(rt)


def choose_service() -> Optional[str]:
    idx = choose("Service", ARGUS_SERVICES)
    if idx is None:
        return None
    return ARGUS_SERVICES[idx]


def menu_lifecycle(rt: Runtime) -> None:
    while True:
        idx = choose(
            "Start / stop / restart",
            [
                "Start stack",
                "Stop stack",
                "Restart all services",
                "Restart one service",
                "Clean volumes and hot-publish output",
            ],
        )
        if idx is None:
            return
        if idx == 0:
            start(rt)
        elif idx == 1:
            stop(rt)
        elif idx == 2:
            restart(rt, [])
        elif idx == 3:
            service = choose_service()
            if service:
                restart(rt, [service])
        elif idx == 4:
            clean(rt)


def menu_config(rt: Runtime) -> None:
    while True:
        idx = choose(
            "Configuration management",
            [
                "Generate local .env profile",
                "Validate config",
                "Show sanitized config summary",
                "Backup config files",
            ],
        )
        if idx is None:
            return
        if idx == 0:
            profile = prompt("Profile", "local")
            init_config(rt, profile)
        elif idx == 1:
            profile = prompt("Profile", "local")
            validate_config(rt, profile)
        elif idx == 2:
            profile = prompt("Profile", "local")
            sanitized_config_summary(rt, profile)
        elif idx == 3:
            backup_config(rt)


def menu_cloud(rt: Runtime) -> None:
    while True:
        idx = choose("Cloud deployments", ["AWS", "Azure plan", "Google Cloud plan", "Kubernetes plan"])
        if idx is None:
            return
        if idx == 0:
            menu_aws(rt)
        elif idx == 1:
            cloud_plan("Azure")
        elif idx == 2:
            cloud_plan("Google Cloud")
        elif idx == 3:
            cloud_plan("Kubernetes")


def menu_aws(rt: Runtime) -> None:
    while True:
        idx = choose(
            "AWS",
            [
                "EC2 + ECS workers deploy",
                "Create ECR repos and push images",
                "Deploy/update ECS services",
                "Run ECS autoscale pass",
                "ECS status",
                "Destroy ECS worker services",
            ],
        )
        if idx is None:
            return
        if idx == 0:
            aws_ecs_workers(rt)
        elif idx == 1:
            aws_ecr_push(rt, [])
        elif idx == 2:
            services = prompt("Services, space-separated; empty means all", "")
            aws_deploy_services(rt, services.split() if services else [])
        elif idx == 3:
            aws_autoscale(rt)
        elif idx == 4:
            aws_status(rt)
        elif idx == 5:
            aws_destroy_workers(rt)


def menu_diagnostics(rt: Runtime) -> None:
    preflight(rt)
    if confirm("Show recent error-like logs?", rt, default=True):
        logs(rt, [], 300, False, True)


def menu_backup(rt: Runtime) -> None:
    while True:
        idx = choose(
            "Backup and rollback",
            [
                "Backup config files",
                "Backup Postgres database with pg_dump",
                "Show rollback guidance",
            ],
        )
        if idx is None:
            return
        if idx == 0:
            backup_config(rt)
        elif idx == 1:
            backup_postgres(rt)
        elif idx == 2:
            rollback_guidance(rt)


def interactive_menu(rt: Runtime) -> None:
    show_header(rt)

    while True:
        idx = choose(
            APP_NAME,
            [
                "Initial deployment",
                "Update Argus Engine",
                "Monitoring and logs",
                "Start / stop / restart",
                "Configuration management",
                "Cloud deployments",
                "Diagnostics and troubleshooting",
                "Backup and rollback",
            ],
        )
        if idx is None:
            print("Bye.")
            return

        try:
            if idx == 0:
                menu_initial(rt)
            elif idx == 1:
                menu_update(rt)
            elif idx == 2:
                menu_monitor(rt)
            elif idx == 3:
                menu_lifecycle(rt)
            elif idx == 4:
                menu_config(rt)
            elif idx == 5:
                menu_cloud(rt)
            elif idx == 6:
                menu_diagnostics(rt)
            elif idx == 7:
                menu_backup(rt)
        except KeyboardInterrupt:
            print_warn("\nCancelled.")
        except subprocess.CalledProcessError as exc:
            print_error(f"Command failed with exit code {exc.returncode}")
        except Exception as exc:
            print_error(f"Error: {exc}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Argus Engine interactive deployment and operations CLI.")
    parser.add_argument("--dry-run", action="store_true", help="Print actions without mutating state.")
    parser.add_argument("--verbose", "-v", action="store_true", help="Print extra command details.")
    parser.add_argument("-y", "--yes", action="store_true", help="Assume yes for prompts. Use carefully.")
    parser.add_argument("--log-file", type=Path, default=None, help="Write deployops log to this path.")

    sub = parser.add_subparsers(dest="command")

    sub.add_parser("menu")
    sub.add_parser("preflight")
    sub.add_parser("doctor")
    sub.add_parser("smoke")
    sub.add_parser("health")
    sub.add_parser("status")
    sub.add_parser("ps")

    deploy_p = sub.add_parser("deploy")
    deploy_p.add_argument("--mode", choices=["hot", "image", "fresh", "ecs-workers"], default="hot")
    deploy_p.add_argument("--no-smoke", action="store_true")

    update_p = sub.add_parser("update")
    update_p.add_argument("--mode", choices=["hot", "image", "fresh"], default="hot")
    update_p.add_argument("--no-pull", action="store_true")
    update_p.add_argument("--no-smoke", action="store_true")

    logs_p = sub.add_parser("logs")
    logs_p.add_argument("services", nargs="*")
    logs_p.add_argument("--tail", "-n", type=int, default=160)
    logs_p.add_argument("--follow", "-f", action="store_true")
    logs_p.add_argument("--errors", action="store_true")

    restart_p = sub.add_parser("restart")
    restart_p.add_argument("services", nargs="*")

    sub.add_parser("start")
    sub.add_parser("stop")
    sub.add_parser("clean")
    sub.add_parser("dev-check")

    config_p = sub.add_parser("config")
    config_sub = config_p.add_subparsers(dest="config_command")
    init_p = config_sub.add_parser("init")
    init_p.add_argument("--profile", default="local")
    validate_p = config_sub.add_parser("validate")
    validate_p.add_argument("--profile", default="local")
    summary_p = config_sub.add_parser("summary")
    summary_p.add_argument("--profile", default="local")
    config_sub.add_parser("backup")

    backup_p = sub.add_parser("backup")
    backup_sub = backup_p.add_subparsers(dest="backup_command")
    backup_sub.add_parser("config")
    backup_sub.add_parser("postgres")

    sub.add_parser("rollback-guide")

    aws_p = sub.add_parser("aws")
    aws_sub = aws_p.add_subparsers(dest="aws_command")
    aws_sub.add_parser("ecs-workers")
    ecr_p = aws_sub.add_parser("ecr-push")
    ecr_p.add_argument("services", nargs="*")
    ecs_p = aws_sub.add_parser("deploy-services")
    ecs_p.add_argument("services", nargs="*")
    aws_sub.add_parser("autoscale")
    aws_sub.add_parser("status")
    aws_sub.add_parser("destroy-workers")

    cloud_p = sub.add_parser("cloud-plan")
    cloud_p.add_argument("provider", choices=["Azure", "Google Cloud", "Kubernetes"])

    return parser


def normalize_legacy_args(argv: list[str]) -> list[str]:
    if not argv:
        return argv

    first = argv[0]

    # deploy.sh invokes deploy-ui.py for these interactive paths.
    # If the user explicitly passed legacy deploy.sh args, execute the equivalent non-interactive action.
    if first in {"up", "--hot", "-hot"}:
        return ["deploy", "--mode", "hot", *argv[1:]]
    if first in {"--image", "-image"}:
        return ["deploy", "--mode", "image", *argv[1:]]
    if first in {"-fresh", "--fresh"}:
        return ["deploy", "--mode", "fresh", *argv[1:]]
    if first == "--ecs-workers":
        return ["deploy", "--mode", "ecs-workers", *argv[1:]]

    return argv


def main(argv: Optional[list[str]] = None) -> int:
    raw_argv = list(sys.argv[1:] if argv is None else argv)

    # With no args from deploy.sh, open the interactive menu.
    if not raw_argv:
        raw_argv = ["menu"]
    else:
        raw_argv = normalize_legacy_args(raw_argv)

    parser = build_parser()
    args = parser.parse_args(raw_argv)

    ctx = RepoContext.discover()
    rt = Runtime(ctx=ctx, dry_run=args.dry_run, verbose=args.verbose, yes=args.yes, log_file=args.log_file)

    try:
        cmd = args.command

        if cmd == "menu":
            interactive_menu(rt)
        elif cmd == "preflight":
            preflight(rt)
        elif cmd == "doctor":
            menu_diagnostics(rt)
        elif cmd == "deploy":
            deploy(rt, args.mode, args.no_smoke)
        elif cmd == "update":
            update(rt, args.mode, pull=not args.no_pull, no_smoke=args.no_smoke)
        elif cmd in {"status", "ps"}:
            status(rt, [])
        elif cmd == "logs":
            logs(rt, args.services, args.tail, args.follow, args.errors)
        elif cmd == "restart":
            restart(rt, args.services)
        elif cmd == "start":
            start(rt)
        elif cmd == "stop":
            stop(rt)
        elif cmd == "clean":
            clean(rt)
        elif cmd == "smoke":
            smoke(rt)
        elif cmd == "health":
            health(rt)
        elif cmd == "dev-check":
            dev_check(rt)
        elif cmd == "config":
            if args.config_command == "init":
                init_config(rt, args.profile)
            elif args.config_command == "validate":
                validate_config(rt, args.profile)
            elif args.config_command == "summary":
                sanitized_config_summary(rt, args.profile)
            elif args.config_command == "backup":
                backup_config(rt)
            else:
                parser.error("config requires a subcommand")
        elif cmd == "backup":
            if args.backup_command == "config":
                backup_config(rt)
            elif args.backup_command == "postgres":
                backup_postgres(rt)
            else:
                parser.error("backup requires a subcommand")
        elif cmd == "rollback-guide":
            rollback_guidance(rt)
        elif cmd == "aws":
            if args.aws_command == "ecs-workers":
                aws_ecs_workers(rt)
            elif args.aws_command == "ecr-push":
                aws_ecr_push(rt, args.services)
            elif args.aws_command == "deploy-services":
                aws_deploy_services(rt, args.services)
            elif args.aws_command == "autoscale":
                aws_autoscale(rt)
            elif args.aws_command == "status":
                aws_status(rt)
            elif args.aws_command == "destroy-workers":
                aws_destroy_workers(rt)
            else:
                parser.error("aws requires a subcommand")
        elif cmd == "cloud-plan":
            cloud_plan(args.provider)
        else:
            parser.print_help()
            return 2

    except KeyboardInterrupt:
        print_warn("\nCancelled.")
        return 130
    except subprocess.CalledProcessError as exc:
        print_error(f"Command failed with exit code {exc.returncode}")
        return exc.returncode
    except Exception as exc:
        print_error(f"Error: {exc}")
        if args.verbose:
            raise
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
''',

    "deploy/DEPLOYOPS_README.md": r'''# Argus Engine DeployOps

This patch replaces `deploy/deploy-ui.py` with a full interactive deployment and operations menu.

The implementation is intentionally dependency-free and uses only the Python standard library. It wraps the existing, battle-tested scripts:

- `deploy/deploy.sh`
- `deploy/logs.sh`
- `deploy/smoke-test.sh`
- `deploy/dev-check.sh`
- `deploy/aws/*.sh`

## Start the menu

From the repository root:

```bash
./deploy/deploy.sh