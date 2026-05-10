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

PORTS = {
    5432: "PostgreSQL",
    6379: "Redis",
    5672: "RabbitMQ",
    15672: "RabbitMQ Management",
    8081: "Gateway",
    8082: "Web",
    8083: "Operations API",
    8084: "Discovery API",
    8085: "Worker Control API",
    8086: "Maintenance API",
    8087: "Updates API",
    8088: "Realtime",
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

@dataclass(frozen=True)
class Repo:
    root: Path
    deploy_dir: Path
    deploy_sh: Path
    logs_sh: Path
    smoke_sh: Path
    dev_check_sh: Path
    compose_file: Path
    aws_dir: Path

    @staticmethod
    def discover() -> "Repo":
        candidates = [Path.cwd(), *Path.cwd().parents, Path(__file__).resolve().parent.parent]
        for root in candidates:
            if (root / "ArgusEngine.slnx").exists() and (root / "deploy").is_dir():
                deploy_dir = root / "deploy"
                return Repo(
                    root=root,
                    deploy_dir=deploy_dir,
                    deploy_sh=deploy_dir / "deploy.sh",
                    logs_sh=deploy_dir / "logs.sh",
                    smoke_sh=deploy_dir / "smoke-test.sh",
                    dev_check_sh=deploy_dir / "dev-check.sh",
                    compose_file=deploy_dir / "docker-compose.yml",
                    aws_dir=deploy_dir / "aws",
                )
        raise SystemExit("Could not find Argus Engine repository root.")

@dataclass
class Runtime:
    repo: Repo
    dry_run: bool = False
    verbose: bool = False
    yes: bool = False

    def __post_init__(self) -> None:
        log_dir = self.repo.deploy_dir / "logs"
        log_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        self.log_file = log_dir / f"deployops_{stamp}.log"

    def log(self, text: str) -> None:
        with self.log_file.open("a", encoding="utf-8") as f:
            f.write(redact(text))

    def run(self, args: list[str], env: dict[str, str] | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
        rendered = " ".join(args)
        self.log("$ " + rendered + "\n")

        if self.dry_run:
            print("[dry-run] " + rendered)
            return subprocess.CompletedProcess(args, 0, "", "")

        merged_env = os.environ.copy()
        if env:
            merged_env.update(env)

        if self.verbose:
            print("Running: " + rendered)

        proc = subprocess.Popen(
            args,
            cwd=str(self.repo.root),
            env=merged_env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )

        output = []
        assert proc.stdout is not None
        for line in proc.stdout:
            output.append(line)
            print(redact(line), end="")

        code = proc.wait()
        text = "".join(output)
        self.log(text)

        if check and code != 0:
            raise subprocess.CalledProcessError(code, args, output=text)

        return subprocess.CompletedProcess(args, code, text, "")

def redact(text: str) -> str:
    secret_words = ("PASSWORD", "SECRET", "TOKEN", "KEY", "CREDENTIAL")
    result = []
    for line in text.splitlines(keepends=True):
        stripped = line.strip()
        if "=" in stripped:
            key, value = stripped.split("=", 1)
            if any(word in key.upper() for word in secret_words):
                result.append(line.replace(value, "***", 1))
                continue
        result.append(line)
    return "".join(result)

def command_path(name: str) -> str:
    return shutil.which(name) or ""

def port_open(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(0.2)
        return sock.connect_ex(("127.0.0.1", port)) == 0

def confirm(message: str, rt: Runtime, default: bool = False) -> bool:
    if rt.yes:
        return True
    suffix = "Y/n" if default else "y/N"
    value = input(f"{message} [{suffix}]: ").strip().lower()
    if not value:
        return default
    return value in {"y", "yes"}

def typed_confirm(message: str, expected: str, rt: Runtime) -> bool:
    if rt.yes:
        return True
    print(message)
    value = input(f"Type {expected!r} to continue: ").strip()
    return value == expected

def choose(title: str, items: list[str]) -> int | None:
    print()
    print("=" * len(title))
    print(title)
    print("=" * len(title))
    for index, item in enumerate(items, 1):
        print(f"[{index}] {item}")
    print("[0] Back/Exit")

    while True:
        raw = input("Choose: ").strip()
        if raw in {"", "0"}:
            return None
        if raw.isdigit() and 1 <= int(raw) <= len(items):
            return int(raw) - 1
        print("Invalid choice.")

def deploy_sh(rt: Runtime, args: list[str], extra_env: dict[str, str] | None = None) -> None:
    env = {"ARGUS_NO_UI": "1"}
    if extra_env:
        env.update(extra_env)
    rt.run(["bash", str(rt.repo.deploy_sh), *args], env=env)

def preflight(rt: Runtime) -> None:
    print()
    print("Argus Engine DeployOps")
    print("repo:    " + str(rt.repo.root))
    print("compose: " + str(rt.repo.compose_file))
    print("log:     " + str(rt.log_file))

    print()
    print("Commands:")
    for cmd in ["bash", "git", "docker", "dotnet", "curl", "aws", "az", "gcloud", "kubectl"]:
        path = command_path(cmd)
        status = "OK" if path else "missing"
        print(f"  {cmd:8} {status:8} {path}")

    print()
    print("Files:")
    for label, path in [
        ("deploy.sh", rt.repo.deploy_sh),
        ("logs.sh", rt.repo.logs_sh),
        ("smoke-test.sh", rt.repo.smoke_sh),
        ("dev-check.sh", rt.repo.dev_check_sh),
        ("docker-compose.yml", rt.repo.compose_file),
        ("aws/", rt.repo.aws_dir),
    ]:
        status = "OK" if path.exists() else "missing"
        print(f"  {label:18} {status}")

    print()
    print("Ports:")
    for port, label in PORTS.items():
        status = "IN USE" if port_open(port) else "free"
        print(f"  {port:<5} {status:8} {label}")

    usage = shutil.disk_usage(str(rt.repo.root))
    print()
    print("Disk:")
    print(f"  total: {usage.total // (1024 ** 3)} GiB")
    print(f"  free:  {usage.free // (1024 ** 3)} GiB")

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

def update(rt: Runtime, mode: str, no_pull: bool = False, no_smoke: bool = False) -> None:
    if not no_pull:
        rt.run(["git", "pull", "--ff-only"])
    deploy(rt, mode, no_smoke=no_smoke)

def status(rt: Runtime) -> None:
    deploy_sh(rt, ["status"])

def logs(rt: Runtime, services: list[str], tail: int, follow: bool, errors: bool) -> None:
    args = ["bash", str(rt.repo.logs_sh), "--tail", str(tail)]
    if follow:
        args.append("--follow")
    if errors:
        args.append("--errors")
    args.extend(services)
    rt.run(args)

def smoke(rt: Runtime) -> None:
    rt.run(["bash", str(rt.repo.smoke_sh)])

def dev_check(rt: Runtime) -> None:
    rt.run(["bash", str(rt.repo.dev_check_sh)])

def start(rt: Runtime) -> None:
    deploy_sh(rt, ["up"])

def stop(rt: Runtime) -> None:
    if confirm("Stop Argus Engine services?", rt):
        deploy_sh(rt, ["down"])

def restart(rt: Runtime, services: list[str]) -> None:
    deploy_sh(rt, ["restart", *services])

def clean(rt: Runtime) -> None:
    if typed_confirm("This removes containers, orphans, volumes, and hot-publish output.", "delete volumes", rt):
        deploy_sh(rt, ["clean"], {"CONFIRM_ARGUS_CLEAN": "yes"})

def config_init(rt: Runtime, profile: str) -> None:
    src = rt.repo.deploy_dir / "config" / f"argus.{profile}.env.example"
    dst = rt.repo.deploy_dir / f".env.{profile}"
    if not src.exists():
        raise SystemExit("Missing template: " + str(src))
    if dst.exists() and not confirm(str(dst) + " exists. Overwrite?", rt):
        return
    if rt.dry_run:
        print("[dry-run] copy " + str(src) + " -> " + str(dst))
        return
    shutil.copy2(src, dst)
    print("Created " + str(dst))

def config_validate(rt: Runtime, profile: str) -> None:
    paths = [rt.repo.deploy_dir / f".env.{profile}", rt.repo.deploy_dir / ".env"]
    env = dict(os.environ)

    for path in paths:
        if not path.exists():
            continue
        for raw in path.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = raw.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                env[key.strip()] = value.strip()

    required = ["ARGUS_ENGINE_VERSION", "ARGUS_DIAGNOSTICS_API_KEY"]
    missing = [key for key in required if not env.get(key)]

    if missing:
        print("Missing variables:")
        for key in missing:
            print("  - " + key)
        raise SystemExit(1)

    print("Config validation OK.")

def backup_config(rt: Runtime) -> None:
    stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    out = rt.repo.deploy_dir / "backups" / stamp
    files = [
        rt.repo.deploy_dir / ".env",
        rt.repo.deploy_dir / ".env.local",
        rt.repo.deploy_dir / ".env.dev",
        rt.repo.deploy_dir / ".env.staging",
        rt.repo.deploy_dir / ".env.production",
        rt.repo.aws_dir / ".env",
        rt.repo.aws_dir / ".env.generated",
        rt.repo.aws_dir / "service-env",
    ]

    if not rt.dry_run:
        out.mkdir(parents=True, exist_ok=True)

    copied = 0
    for src in files:
        if not src.exists():
            continue
        dst = out / src.relative_to(rt.repo.root)
        if rt.dry_run:
            print("[dry-run] copy " + str(src) + " -> " + str(dst))
        else:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
        copied += 1

    print(f"Backed up {copied} file(s) to {out}")

def aws(rt: Runtime, action: str, services: list[str]) -> None:
    if action == "ecs-workers":
        deploy(rt, "ecs-workers", no_smoke=True)
    elif action == "ecr-push":
        rt.run(["bash", str(rt.repo.aws_dir / "create-ecr-repos.sh")])
        rt.run(["bash", str(rt.repo.aws_dir / "build-push-ecr.sh"), *services])
    elif action == "deploy-services":
        rt.run(["bash", str(rt.repo.aws_dir / "deploy-ecs-services.sh"), *services])
    elif action == "autoscale":
        rt.run(["bash", str(rt.repo.aws_dir / "autoscale-ecs-workers.sh")])
    elif action == "status":
        script = rt.repo.aws_dir / "ecs-command-center-status.sh"
        if script.exists():
            rt.run(["bash", str(script)], check=False)
        else:
            print("AWS status script not found.")
    elif action == "destroy-workers":
        if typed_confirm("Destroy ECS worker services only.", "destroy workers", rt):
            rt.run(["bash", str(rt.repo.aws_dir / "destroy-ecs-services.sh"), "workers"], env={"CONFIRM_DESTROY_ECS_WORKERS": "yes"})

def rollback_guide() -> None:
    print("Rollback guide:")
    print("  git log --oneline -20")
    print("  python3 deploy/deploy-ui.py backup config")
    print("  ARGUS_NO_UI=1 ./deploy/deploy.sh down")
    print("  git checkout <known-good-sha>")
    print("  ARGUS_NO_UI=1 ./deploy/deploy.sh --image up")
    print("  ./deploy/smoke-test.sh")

def service_menu() -> list[str]:
    idx = choose("Service", SERVICES)
    if idx is None:
        return []
    return [SERVICES[idx]]

def menu(rt: Runtime) -> None:
    while True:
        idx = choose("Argus Engine DeployOps", [
            "Preflight checks",
            "Initial deploy hot",
            "Initial deploy fresh",
            "Update hot",
            "Status",
            "Logs",
            "Follow service logs",
            "Error logs",
            "Restart all",
            "Restart one service",
            "Stop",
            "Start",
            "Smoke test",
            "Dev check",
            "Config init local",
            "Config validate local",
            "Backup config",
            "AWS ECS workers",
            "Rollback guide",
            "Clean volumes",
        ])

        if idx is None:
            return

        if idx == 0:
            preflight(rt)
        elif idx == 1:
            deploy(rt, "hot")
        elif idx == 2:
            deploy(rt, "fresh")
        elif idx == 3:
            update(rt, "hot")
        elif idx == 4:
            status(rt)
        elif idx == 5:
            logs(rt, [], 160, False, False)
        elif idx == 6:
            logs(rt, service_menu(), 160, True, False)
        elif idx == 7:
            logs(rt, [], 300, False, True)
        elif idx == 8:
            restart(rt, [])
        elif idx == 9:
            restart(rt, service_menu())
        elif idx == 10:
            stop(rt)
        elif idx == 11:
            start(rt)
        elif idx == 12:
            smoke(rt)
        elif idx == 13:
            dev_check(rt)
        elif idx == 14:
            config_init(rt, "local")
        elif idx == 15:
            config_validate(rt, "local")
        elif idx == 16:
            backup_config(rt)
        elif idx == 17:
            aws(rt, "ecs-workers", [])
        elif idx == 18:
            rollback_guide()
        elif idx == 19:
            clean(rt)

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Argus Engine DeployOps")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--verbose", "-v", action="store_true")
    parser.add_argument("--yes", "-y", action="store_true")
    sub = parser.add_subparsers(dest="cmd")

    sub.add_parser("menu")
    sub.add_parser("preflight")
    sub.add_parser("status")
    sub.add_parser("start")
    sub.add_parser("stop")
    sub.add_parser("clean")
    sub.add_parser("smoke")
    sub.add_parser("dev-check")
    sub.add_parser("rollback-guide")

    deploy_parser = sub.add_parser("deploy")
    deploy_parser.add_argument("--mode", choices=["hot", "image", "fresh", "ecs-workers"], default="hot")
    deploy_parser.add_argument("--no-smoke", action="store_true")

    update_parser = sub.add_parser("update")
    update_parser.add_argument("--mode", choices=["hot", "image", "fresh"], default="hot")
    update_parser.add_argument("--no-pull", action="store_true")
    update_parser.add_argument("--no-smoke", action="store_true")

    logs_parser = sub.add_parser("logs")
    logs_parser.add_argument("services", nargs="*")
    logs_parser.add_argument("--tail", "-n", type=int, default=160)
    logs_parser.add_argument("--follow", "-f", action="store_true")
    logs_parser.add_argument("--errors", action="store_true")

    restart_parser = sub.add_parser("restart")
    restart_parser.add_argument("services", nargs="*")

    config_parser = sub.add_parser("config")
    config_sub = config_parser.add_subparsers(dest="config_cmd")
    config_init_parser = config_sub.add_parser("init")
    config_init_parser.add_argument("--profile", default="local")
    config_validate_parser = config_sub.add_parser("validate")
    config_validate_parser.add_argument("--profile", default="local")
    config_sub.add_parser("backup")

    backup_parser = sub.add_parser("backup")
    backup_sub = backup_parser.add_subparsers(dest="backup_cmd")
    backup_sub.add_parser("config")

    aws_parser = sub.add_parser("aws")
    aws_sub = aws_parser.add_subparsers(dest="aws_cmd")
    aws_sub.add_parser("ecs-workers")
    ecr_parser = aws_sub.add_parser("ecr-push")
    ecr_parser.add_argument("services", nargs="*")
    deploy_services_parser = aws_sub.add_parser("deploy-services")
    deploy_services_parser.add_argument("services", nargs="*")
    aws_sub.add_parser("autoscale")
    aws_sub.add_parser("status")
    aws_sub.add_parser("destroy-workers")

    return parser

def normalize(argv: list[str]) -> list[str]:
    if not argv:
        return ["menu"]
    if argv[0] in {"up", "--hot", "-hot"}:
        return ["deploy", "--mode", "hot", *argv[1:]]
    if argv[0] in {"--image", "-image"}:
        return ["deploy", "--mode", "image", *argv[1:]]
    if argv[0] in {"-fresh", "--fresh"}:
        return ["deploy", "--mode", "fresh", *argv[1:]]
    if argv[0] == "--ecs-workers":
        return ["deploy", "--mode", "ecs-workers", *argv[1:]]
    return argv

def main() -> int:
    parser = build_parser()
    args = parser.parse_args(normalize(sys.argv[1:]))
    rt = Runtime(Repo.discover(), dry_run=args.dry_run, verbose=args.verbose, yes=args.yes)

    try:
        if args.cmd == "menu":
            menu(rt)
        elif args.cmd == "preflight":
            preflight(rt)
        elif args.cmd == "deploy":
            deploy(rt, args.mode, args.no_smoke)
        elif args.cmd == "update":
            update(rt, args.mode, args.no_pull, args.no_smoke)
        elif args.cmd == "status":
            status(rt)
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
            elif args.config_cmd == "backup":
                backup_config(rt)
            else:
                parser.error("config requires a subcommand")
        elif args.cmd == "backup":
            if args.backup_cmd == "config":
                backup_config(rt)
            else:
                parser.error("backup requires a subcommand")
        elif args.cmd == "aws":
            if not args.aws_cmd:
                parser.error("aws requires a subcommand")
            aws(rt, args.aws_cmd, getattr(args, "services", []))
        else:
            parser.print_help()
            return 2
    except KeyboardInterrupt:
        print("Cancelled.")
        return 130
    except subprocess.CalledProcessError as exc:
        print(f"Command failed with exit code {exc.returncode}", file=sys.stderr)
        return exc.returncode

    return 0

if __name__ == "__main__":
    raise SystemExit(main())
