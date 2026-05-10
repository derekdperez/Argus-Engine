$ErrorActionPreference = "Stop"

$ZipName = "argus-engine-deployops-patch.zip"
$Stage = Join-Path $env:TEMP ("argus-deployops-patch-" + [guid]::NewGuid())

if (Test-Path $ZipName) {
    Remove-Item $ZipName -Force
}

New-Item -ItemType Directory -Force -Path $Stage | Out-Null

function Write-LfFile {
    param(
        [Parameter(Mandatory=$true)][string]$RelativePath,
        [Parameter(Mandatory=$true)][string]$Content
    )

    $Target = Join-Path $Stage $RelativePath
    $Dir = Split-Path $Target -Parent
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null

    $Normalized = $Content -replace "`r`n", "`n"
    $Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Target, $Normalized, $Utf8NoBom)
}

Write-LfFile "deploy/deploy-ui.py" @'
#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import shutil
import socket
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable, Optional


APP_NAME = "Argus Engine DeployOps"

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

SERVICES = [
    "postgres",
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

SECRET_WORDS = ("PASSWORD", "SECRET", "TOKEN", "KEY", "CREDENTIAL", "PRIVATE")


@dataclass(frozen=True)
class Repo:
    root: Path
    deploy: Path
    compose: Path
    deploy_sh: Path
    logs_sh: Path
    smoke_sh: Path
    dev_check_sh: Path
    aws: Path

    @staticmethod
    def discover() -> "Repo":
        start = Path(__file__).resolve().parent.parent
        for candidate in [start, Path.cwd(), *Path.cwd().parents]:
            if (candidate / "ArgusEngine.slnx").exists() and (candidate / "deploy").is_dir():
                deploy = candidate / "deploy"
                return Repo(
                    root=candidate,
                    deploy=deploy,
                    compose=deploy / "docker-compose.yml",
                    deploy_sh=deploy / "deploy.sh",
                    logs_sh=deploy / "logs.sh",
                    smoke_sh=deploy / "smoke-test.sh",
                    dev_check_sh=deploy / "dev-check.sh",
                    aws=deploy / "aws",
                )
        raise RuntimeError("Could not find Argus Engine repository root.")


@dataclass
class Runtime:
    repo: Repo
    dry_run: bool = False
    verbose: bool = False
    yes: bool = False
    log_file: Optional[Path] = None

    def __post_init__(self) -> None:
        if self.log_file is None:
            log_dir = self.repo.deploy / "logs"
            log_dir.mkdir(parents=True, exist_ok=True)
            stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
            self.log_file = log_dir / f"deployops_{stamp}.log"

    def log(self, text: str) -> None:
        assert self.log_file is not None
        with self.log_file.open("a", encoding="utf-8") as fh:
            fh.write(redact(text))
            if not text.endswith("\n"):
                fh.write("\n")

    def run(
        self,
        args: list[str],
        *,
        env: Optional[dict[str, str]] = None,
        check: bool = True,
        stream: bool = True,
    ) -> subprocess.CompletedProcess[str]:
        rendered = " ".join(quote(a) for a in args)
        self.log("$ " + rendered)

        if self.dry_run:
            warn("DRY RUN: " + rendered)
            return subprocess.CompletedProcess(args, 0, "", "")

        merged_env = os.environ.copy()
        if env:
            merged_env.update(env)

        if self.verbose:
            dim("Running: " + rendered)

        if stream:
            proc = subprocess.Popen(
                args,
                cwd=str(self.repo.root),
                env=merged_env,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
            )
            assert proc.stdout is not None
            out = []
            for line in proc.stdout:
                out.append(line)
                print(redact(line), end="")
            code = proc.wait()
            output = "".join(out)
            self.log(output)
            if check and code != 0:
                raise subprocess.CalledProcessError(code, args, output=output)
            return subprocess.CompletedProcess(args, code, output, "")

        cp = subprocess.run(
            args,
            cwd=str(self.repo.root),
            env=merged_env,
            capture_output=True,
            text=True,
            check=False,
        )
        self.log(cp.stdout)
        self.log(cp.stderr)
        if check and cp.returncode != 0:
            raise subprocess.CalledProcessError(cp.returncode, args, output=cp.stdout, stderr=cp.stderr)
        return cp


def supports_color() -> bool:
    return sys.stdout.isatty() and os.environ.get("NO_COLOR") is None


def c(text: str, code: str) -> str:
    return f"\033[{code}m{text}\033[0m" if supports_color() else text


def ok(text: str) -> None:
    print(c(text, "32"))


def warn(text: str) -> None:
    print(c(text, "33"))


def err(text: str) -> None:
    print(c(text, "31"), file=sys.stderr)


def dim(text: str) -> None:
    print(c(text, "2"))


def title(text: str) -> None:
    print()
    print(c("=" * len(text), "36"))
    print(c(text, "36;1"))
    print(c("=" * len(text), "36"))


def quote(s: str) -> str:
    safe = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_+-=./:@\\"
    if s and all(ch in safe for ch in s):
        return s
    return "'" + s.replace("'", "'\"'\"'") + "'"


def redact(text: str) -> str:
    lines = []
    for line in text.splitlines(keepends=True):
        stripped = line.strip()
        if "=" in stripped:
            key, value = stripped.split("=", 1)
            if any(word in key.upper() for word in SECRET_WORDS):
                lines.append(line.replace(value, "***", 1))
                continue
        lines.append(line)
    return "".join(lines)


def command_exists(name: str) -> bool:
    return shutil.which(name) is not None


def port_in_use(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.25)
        return sock.connect_ex(("127.0.0.1", port)) == 0


def parse_env(path: Path) -> dict[str, str]:
    data: dict[str, str] = {}
    if not path.exists():
        return data
    for raw in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip()] = value.strip().strip('"').strip("'")
    return data


def confirm(text: str, rt: Runtime, default: bool = False) -> bool:
    if rt.yes:
        return True
    marker = "Y/n" if default else "y/N"
    raw = input(f"{text} [{marker}]: ").strip().lower()
    if not raw:
        return default
    return raw in {"y", "yes"}


def typed_confirm(text: str, expected: str, rt: Runtime) -> bool:
    if rt.yes:
        return True
    warn(text)
    raw = input(f"Type {expected!r} to continue: ").strip()
    return raw == expected


def choose(label: str, choices: list[str]) -> Optional[int]:
    title(label)
    for i, choice in enumerate(choices, start=1):
        print(f"[{i}] {choice}")
    print("[0] Back/Exit")
    while True:
        raw = input("Choose: ").strip()
        if raw in {"", "0"}:
            return None
        if raw.isdigit() and 1 <= int(raw) <= len(choices):
            return int(raw) - 1
        warn("Invalid selection.")


def ensure(path: Path, label: str) -> None:
    if not path.exists():
        raise FileNotFoundError(f"{label} not found: {path}")


def header(rt: Runtime) -> None:
    title(APP_NAME)
    print(f"repo:     {rt.repo.root}")
    print(f"compose:  {rt.repo.compose}")
    print(f"dry-run:  {rt.dry_run}")
    print(f"verbose:  {rt.verbose}")
    print(f"log file: {rt.log_file}")


def deploy_sh(rt: Runtime, args: Iterable[str], env: Optional[dict[str, str]] = None) -> None:
    ensure(rt.repo.deploy_sh, "deploy.sh")
    merged = {"ARGUS_NO_UI": "1"}
    if env:
        merged.update(env)
    rt.run(["bash", str(rt.repo.deploy_sh), *args], env=merged)


def preflight(rt: Runtime) -> None:
    header(rt)

    title("Command checks")
    for name in ["bash", "git", "docker", "dotnet", "curl", "aws", "az", "gcloud", "kubectl"]:
        path = shutil.which(name)
        print(f"{name:10} {'OK' if path else 'missing':8} {path or ''}")

    title("Repository files")
    for label, path in [
        ("solution", rt.repo.root / "ArgusEngine.slnx"),
        ("compose", rt.repo.compose),
        ("deploy.sh", rt.repo.deploy_sh),
        ("logs.sh", rt.repo.logs_sh),
        ("smoke-test.sh", rt.repo.smoke_sh),
        ("dev-check.sh", rt.repo.dev_check_sh),
        ("aws dir", rt.repo.aws),
    ]:
        print(f"{label:16} {'OK' if path.exists() else 'missing':8} {path}")

    title("Port checks")
    for port, label in DEFAULT_PORTS.items():
        print(f"{port:<6} {'IN USE' if port_in_use(port) else 'free':<8} {label}")

    title("Disk")
    usage = shutil.disk_usage(str(rt.repo.root))
    print(f"total: {usage.total // (1024 ** 3)} GiB")
    print(f"used:  {usage.used // (1024 ** 3)} GiB")
    print(f"free:  {usage.free // (1024 ** 3)} GiB")


def deploy(rt: Runtime, mode: str, no_smoke: bool = False) -> None:
    modes = {
        "hot": ["--hot", "up"],
        "image": ["--image", "up"],
        "fresh": ["-fresh"],
        "ecs-workers": ["--ecs-workers"],
    }
    deploy_sh(rt, modes[mode])
    if not no_smoke and mode != "ecs-workers":
        smoke(rt)


def update(rt: Runtime, mode: str, pull: bool = True, no_smoke: bool = False) -> None:
    if pull:
        dirty = rt.run(["git", "status", "--porcelain"], check=False, stream=False).stdout.strip()
        if dirty:
            warn("Working tree has uncommitted changes:")
            print(dirty)
            if not confirm("Continue anyway?", rt):
                return
        rt.run(["git", "pull", "--ff-only"])
    deploy(rt, mode, no_smoke=no_smoke)


def status(rt: Runtime, services: list[str]) -> None:
    deploy_sh(rt, ["status", *services])


def logs(rt: Runtime, services: list[str], tail: int, follow: bool, errors_only: bool) -> None:
    ensure(rt.repo.logs_sh, "logs.sh")
    args = ["bash", str(rt.repo.logs_sh), "--tail", str(tail)]
    if follow:
        args.append("--follow")
    if errors_only:
        args.append("--errors")
    args.extend(services)
    rt.run(args)


def start(rt: Runtime) -> None:
    deploy_sh(rt, ["up"])


def stop(rt: Runtime) -> None:
    if confirm("Stop Argus Engine services?", rt):
        deploy_sh(rt, ["down"])


def restart(rt: Runtime, services: list[str]) -> None:
    deploy_sh(rt, ["restart", *services])


def clean(rt: Runtime) -> None:
    if typed_confirm("This removes containers, orphans, volumes, and hot-publish output.", "delete volumes", rt):
        deploy_sh(rt, ["clean"], env={"CONFIRM_ARGUS_CLEAN": "yes"})


def smoke(rt: Runtime) -> None:
    ensure(rt.repo.smoke_sh, "smoke-test.sh")
    rt.run(["bash", str(rt.repo.smoke_sh)])


def dev_check(rt: Runtime) -> None:
    ensure(rt.repo.dev_check_sh, "dev-check.sh")
    rt.run(["bash", str(rt.repo.dev_check_sh)])


def config_init(rt: Runtime, profile: str) -> None:
    src = rt.repo.deploy / "config" / f"argus.{profile}.env.example"
    dst = rt.repo.deploy / f".env.{profile}"
    ensure(src, f"{profile} config template")
    if dst.exists() and not confirm(f"{dst} exists. Overwrite?", rt):
        return
    if rt.dry_run:
        warn(f"DRY RUN: copy {src} -> {dst}")
        return
    shutil.copy2(src, dst)
    ok(f"Created {dst}")


def config_validate(rt: Runtime, profile: str) -> None:
    title(f"Config validation: {profile}")
    env = {}
    env.update(parse_env(rt.repo.deploy / f".env.{profile}"))
    env.update(parse_env(rt.repo.deploy / ".env"))
    env.update({k: v for k, v in os.environ.items() if k.startswith("ARGUS_")})

    required = ["ARGUS_ENGINE_VERSION", "ARGUS_DIAGNOSTICS_API_KEY"]
    missing = [k for k in required if not env.get(k)]
    if missing:
        warn("Missing local variables:")
        for key in missing:
            print(f"  - {key}")
    else:
        ok("Local variables look present.")

    aws_env = parse_env(rt.repo.aws / ".env")
    if (rt.repo.aws / ".env").exists():
        aws_required = [
            "AWS_REGION",
            "AWS_ACCOUNT_ID",
            "ECS_CLUSTER",
            "ECS_SUBNETS",
            "ECS_SECURITY_GROUPS",
            "ECS_TASK_EXECUTION_ROLE_ARN",
        ]
        aws_missing = [k for k in aws_required if not aws_env.get(k)]
        if aws_missing:
            warn("Missing AWS variables:")
            for key in aws_missing:
                print(f"  - {key}")
        else:
            ok("AWS variables look present.")
    else:
        warn("deploy/aws/.env not found. AWS workflows may need it.")


def config_summary(rt: Runtime, profile: str) -> None:
    title(f"Sanitized config summary: {profile}")
    for path in [
        rt.repo.deploy / f".env.{profile}",
        rt.repo.deploy / ".env",
        rt.repo.aws / ".env",
        rt.repo.aws / "service-env",
        rt.repo.aws / ".env.generated",
    ]:
        if not path.exists():
            continue
        print(f"\n# {path.relative_to(rt.repo.root)}")
        for key, value in sorted(parse_env(path).items()):
            if any(word in key.upper() for word in SECRET_WORDS):
                value = "***"
            print(f"{key}={value}")


def backup_config(rt: Runtime) -> None:
    stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    out_dir = rt.repo.deploy / "backups" / stamp
    files = [
        rt.repo.deploy / ".env",
        rt.repo.deploy / ".env.local",
        rt.repo.deploy / ".env.dev",
        rt.repo.deploy / ".env.staging",
        rt.repo.deploy / ".env.production",
        rt.repo.aws / ".env",
        rt.repo.aws / ".env.generated",
        rt.repo.aws / "service-env",
    ]
    if rt.dry_run:
        warn(f"DRY RUN: mkdir {out_dir}")
    else:
        out_dir.mkdir(parents=True, exist_ok=True)

    copied = 0
    for src in files:
        if not src.exists():
            continue
        dst = out_dir / src.relative_to(rt.repo.root)
        if rt.dry_run:
            warn(f"DRY RUN: copy {src} -> {dst}")
        else:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
        copied += 1
    ok(f"Backed up {copied} config file(s) to {out_dir}")


def rollback_guide() -> None:
    title("Rollback guidance")
    print("""Local rollback:
  git log --oneline -20
  python3 deploy/deploy-ui.py backup config
  ARGUS_NO_UI=1 ./deploy/deploy.sh down
  git checkout <known-good-sha>
  ARGUS_NO_UI=1 ./deploy/deploy.sh --image up
  ./deploy/smoke-test.sh

ECS rollback:
  Prefer immutable image tags and previous ECS task-definition revisions.
  Avoid rebuilding a mutable latest tag during incident response.
""")


def aws(rt: Runtime, action: str, services: list[str]) -> None:
    scripts = {
        "ecr-push": ["create-ecr-repos.sh", "build-push-ecr.sh"],
        "deploy-services": ["deploy-ecs-services.sh"],
        "autoscale": ["autoscale-ecs-workers.sh"],
        "destroy-workers": ["destroy-ecs-services.sh"],
    }

    if action == "ecs-workers":
        if confirm("Deploy EC2-hosted core stack with ECS workers?", rt):
            deploy(rt, "ecs-workers", no_smoke=True)
        return

    if action == "status":
        script = rt.repo.aws / "ecs-command-center-status.sh"
        if script.exists():
            rt.run(["bash", str(script)], check=False)
        else:
            warn("deploy/aws/ecs-command-center-status.sh not found.")
        return

    if action == "ecr-push":
        for script in scripts[action]:
            ensure(rt.repo.aws / script, script)
        rt.run(["bash", str(rt.repo.aws / "create-ecr-repos.sh")])
        rt.run(["bash", str(rt.repo.aws / "build-push-ecr.sh"), *services])
        return

    if action == "deploy-services":
        ensure(rt.repo.aws / "deploy-ecs-services.sh", "deploy-ecs-services.sh")
        rt.run(["bash", str(rt.repo.aws / "deploy-ecs-services.sh"), *services])
        return

    if action == "autoscale":
        ensure(rt.repo.aws / "autoscale-ecs-workers.sh", "autoscale-ecs-workers.sh")
        rt.run(["bash", str(rt.repo.aws / "autoscale-ecs-workers.sh")])
        return

    if action == "destroy-workers":
        ensure(rt.repo.aws / "destroy-ecs-services.sh", "destroy-ecs-services.sh")
        if typed_confirm("Destroy ECS worker services only.", "destroy workers", rt):
            rt.run(
                ["bash", str(rt.repo.aws / "destroy-ecs-services.sh"), "workers"],
                env={"CONFIRM_DESTROY_ECS_WORKERS": "yes"},
            )


def cloud_plan(provider: str) -> None:
    title(provider + " plan")
    if provider == "azure":
        print("""No complete Azure deployment baseline is assumed by this wrapper.
Recommended path:
  1. Azure VM + Docker Compose parity.
  2. ACR for images.
  3. Azure Container Apps for stateless APIs/workers.
  4. Azure Database for PostgreSQL, Azure Cache for Redis, Key Vault, Monitor.
  5. Keep RabbitMQ containerized unless compatibility is proven with another AMQP option.
""")
    elif provider == "gcp":
        print("""No complete GCP deployment baseline is assumed by this wrapper.
Recommended path:
  1. Compute Engine + Docker Compose parity.
  2. Artifact Registry for images.
  3. Cloud Run for stateless APIs/workers where compatible.
  4. Cloud SQL for PostgreSQL, Memorystore for Redis, Secret Manager, Cloud Logging.
  5. Keep RabbitMQ on Compute Engine or move to GKE.
""")
    else:
        print("""No Helm chart or Kubernetes manifests are assumed by this wrapper.
Recommended path:
  1. Add deploy/helm/argus-engine.
  2. Use external Postgres/Redis/RabbitMQ for production.
  3. Add liveness/readiness probes for /health and /health/ready.
  4. Add rollout restart and rollback helpers.
""")


def service_menu() -> Optional[str]:
    idx = choose("Service", SERVICES)
    if idx is None:
        return None
    return SERVICES[idx]


def menu(rt: Runtime) -> None:
    header(rt)
    while True:
        idx = choose(APP_NAME, [
            "Initial deployment",
            "Update Argus Engine",
            "Monitoring and logs",
            "Start / stop / restart",
            "Configuration management",
            "Cloud deployments",
            "Diagnostics and troubleshooting",
            "Backup and rollback",
        ])
        if idx is None:
            print("Bye.")
            return

        try:
            if idx == 0:
                sub = choose("Initial deployment", [
                    "Preflight checks",
                    "Local Docker Compose hot deploy",
                    "Fresh rebuild",
                    "Dev check",
                    "Smoke test",
                ])
                if sub == 0:
                    preflight(rt)
                elif sub == 1:
                    deploy(rt, "hot")
                elif sub == 2:
                    deploy(rt, "fresh")
                elif sub == 3:
                    dev_check(rt)
                elif sub == 4:
                    smoke(rt)

            elif idx == 1:
                sub = choose("Update", ["Hot update", "Image update", "Fresh rebuild", "Git pull only"])
                if sub == 0:
                    update(rt, "hot")
                elif sub == 1:
                    update(rt, "image")
                elif sub == 2:
                    update(rt, "fresh")
                elif sub == 3:
                    rt.run(["git", "pull", "--ff-only"])

            elif idx == 2:
                sub = choose("Monitoring", [
                    "Status",
                    "Logs",
                    "Follow logs for service",
                    "Error-only logs",
                    "Smoke test",
                ])
                if sub == 0:
                    status(rt, [])
                elif sub == 1:
                    logs(rt, [], 160, False, False)
                elif sub == 2:
                    svc = service_menu()
                    if svc:
                        logs(rt, [svc], 160, True, False)
                elif sub == 3:
                    logs(rt, [], 300, False, True)
                elif sub == 4:
                    smoke(rt)

            elif idx == 3:
                sub = choose("Lifecycle", [
                    "Start stack",
                    "Stop stack",
                    "Restart all",
                    "Restart one service",
                    "Clean volumes",
                ])
                if sub == 0:
                    start(rt)
                elif sub == 1:
                    stop(rt)
                elif sub == 2:
                    restart(rt, [])
                elif sub == 3:
                    svc = service_menu()
                    if svc:
                        restart(rt, [svc])
                elif sub == 4:
                    clean(rt)

            elif idx == 4:
                sub = choose("Configuration", [
                    "Generate env profile",
                    "Validate config",
                    "Sanitized config summary",
                    "Backup config",
                ])
                profile = "local"
                if sub in {0, 1, 2}:
                    profile = input("Profile [local]: ").strip() or "local"
                if sub == 0:
                    config_init(rt, profile)
                elif sub == 1:
                    config_validate(rt, profile)
                elif sub == 2:
                    config_summary(rt, profile)
                elif sub == 3:
                    backup_config(rt)

            elif idx == 5:
                sub = choose("Cloud", [
                    "AWS EC2 + ECS workers",
                    "AWS ECR push",
                    "AWS deploy ECS services",
                    "AWS autoscale",
                    "AWS status",
                    "AWS destroy workers",
                    "Azure plan",
                    "GCP plan",
                    "Kubernetes plan",
                ])
                if sub == 0:
                    aws(rt, "ecs-workers", [])
                elif sub == 1:
                    aws(rt, "ecr-push", [])
                elif sub == 2:
                    raw = input("Services, space-separated; empty means all: ").strip()
                    aws(rt, "deploy-services", raw.split() if raw else [])
                elif sub == 3:
                    aws(rt, "autoscale", [])
                elif sub == 4:
                    aws(rt, "status", [])
                elif sub == 5:
                    aws(rt, "destroy-workers", [])
                elif sub == 6:
                    cloud_plan("azure")
                elif sub == 7:
                    cloud_plan("gcp")
                elif sub == 8:
                    cloud_plan("kubernetes")

            elif idx == 6:
                preflight(rt)
                if confirm("Show recent error-like logs?", rt, default=True):
                    logs(rt, [], 300, False, True)

            elif idx == 7:
                sub = choose("Backup and rollback", [
                    "Backup config",
                    "Rollback guidance",
                ])
                if sub == 0:
                    backup_config(rt)
                elif sub == 1:
                    rollback_guide()

        except KeyboardInterrupt:
            warn("\nCancelled.")
        except subprocess.CalledProcessError as exc:
            err(f"Command failed with exit code {exc.returncode}")
        except Exception as exc:
            err(f"Error: {exc}")


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Argus Engine DeployOps")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--verbose", "-v", action="store_true")
    p.add_argument("--yes", "-y", action="store_true")
    p.add_argument("--log-file", type=Path)
    sub = p.add_subparsers(dest="cmd")

    sub.add_parser("menu")
    sub.add_parser("preflight")
    sub.add_parser("doctor")
    sub.add_parser("status")
    sub.add_parser("ps")
    sub.add_parser("start")
    sub.add_parser("stop")
    sub.add_parser("clean")
    sub.add_parser("smoke")
    sub.add_parser("dev-check")
    sub.add_parser("rollback-guide")

    d = sub.add_parser("deploy")
    d.add_argument("--mode", choices=["hot", "image", "fresh", "ecs-workers"], default="hot")
    d.add_argument("--no-smoke", action="store_true")

    u = sub.add_parser("update")
    u.add_argument("--mode", choices=["hot", "image", "fresh"], default="hot")
    u.add_argument("--no-pull", action="store_true")
    u.add_argument("--no-smoke", action="store_true")

    l = sub.add_parser("logs")
    l.add_argument("services", nargs="*")
    l.add_argument("--tail", "-n", type=int, default=160)
    l.add_argument("--follow", "-f", action="store_true")
    l.add_argument("--errors", action="store_true")

    r = sub.add_parser("restart")
    r.add_argument("services", nargs="*")

    cfg = sub.add_parser("config")
    cfg_sub = cfg.add_subparsers(dest="config_cmd")
    ci = cfg_sub.add_parser("init")
    ci.add_argument("--profile", default="local")
    cv = cfg_sub.add_parser("validate")
    cv.add_argument("--profile", default="local")
    cs = cfg_sub.add_parser("summary")
    cs.add_argument("--profile", default="local")
    cfg_sub.add_parser("backup")

    b = sub.add_parser("backup")
    b_sub = b.add_subparsers(dest="backup_cmd")
    b_sub.add_parser("config")

    a = sub.add_parser("aws")
    a_sub = a.add_subparsers(dest="aws_cmd")
    a_sub.add_parser("ecs-workers")
    ep = a_sub.add_parser("ecr-push")
    ep.add_argument("services", nargs="*")
    ds = a_sub.add_parser("deploy-services")
    ds.add_argument("services", nargs="*")
    a_sub.add_parser("autoscale")
    a_sub.add_parser("status")
    a_sub.add_parser("destroy-workers")

    cp = sub.add_parser("cloud-plan")
    cp.add_argument("provider", choices=["azure", "gcp", "kubernetes"])

    return p


def normalize_legacy_args(argv: list[str]) -> list[str]:
    if not argv:
        return ["menu"]
    first = argv[0]
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
    argv = normalize_legacy_args(list(sys.argv[1:] if argv is None else argv))
    p = parser()
    args = p.parse_args(argv)

    repo = Repo.discover()
    rt = Runtime(repo, dry_run=args.dry_run, verbose=args.verbose, yes=args.yes, log_file=args.log_file)

    try:
        if args.cmd == "menu":
            menu(rt)
        elif args.cmd == "preflight":
            preflight(rt)
        elif args.cmd == "doctor":
            preflight(rt)
            logs(rt, [], 300, False, True)
        elif args.cmd == "deploy":
            deploy(rt, args.mode, args.no_smoke)
        elif args.cmd == "update":
            update(rt, args.mode, pull=not args.no_pull, no_smoke=args.no_smoke)
        elif args.cmd in {"status", "ps"}:
            status(rt, [])
        elif args.cmd == "logs":
            logs(rt, args.services, args.tail, args.follow, args.errors)
        elif args.cmd == "restart":
            restart(rt, args.services)
        elif args.cmd == "start":
            start(rt)
        elif args.cmd == "stop":
            stop(rt)
        elif args.cmd == "clean":
            clean(rt)
        elif args.cmd == "smoke":
            smoke(rt)
        elif args.cmd == "dev-check":
            dev_check(rt)
        elif args.cmd == "rollback-guide":
            rollback_guide()
        elif args.cmd == "config":
            if args.config_cmd == "init":
                config_init(rt, args.profile)
            elif args.config_cmd == "validate":
                config_validate(rt, args.profile)
            elif args.config_cmd == "summary":
                config_summary(rt, args.profile)
            elif args.config_cmd == "backup":
                backup_config(rt)
            else:
                p.error("config requires a subcommand")
        elif args.cmd == "backup":
            if args.backup_cmd == "config":
                backup_config(rt)
            else:
                p.error("backup requires a subcommand")
        elif args.cmd == "aws":
            if not args.aws_cmd:
                p.error("aws requires a subcommand")
            aws(rt, args.aws_cmd, getattr(args, "services", []))
        elif args.cmd == "cloud-plan":
            cloud_plan(args.provider)
        else:
            p.print_help()
            return 2
    except KeyboardInterrupt:
        warn("\nCancelled.")
        return 130
    except subprocess.CalledProcessError as exc:
        err(f"Command failed with exit code {exc.returncode}")
        return exc.returncode
    except Exception as exc:
        err(f"Error: {exc}")
        if args.verbose:
            raise
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
'@

Write-LfFile "deploy/DEPLOYOPS_README.md" @'
# Argus Engine DeployOps

This patch replaces `deploy/deploy-ui.py` with a standard-library Python interactive deployment and operations menu.

It wraps the existing repository scripts instead of replacing them:

- `deploy/deploy.sh`
- `deploy/logs.sh`
- `deploy/smoke-test.sh`
- `deploy/dev-check.sh`
- `deploy/aws/*.sh`

## Start

```bash
./deploy/deploy.sh