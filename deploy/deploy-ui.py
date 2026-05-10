#!/usr/bin/env python3
"""Compatibility shim.

The deployment UI has moved to the .NET console project at
deploy/ArgusEngine.DeployUi. This file remains only because older deploy.sh
versions execute deploy-ui.py directly when running in an interactive terminal.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path


def resolve_dotnet() -> str | None:
    candidate = shutil.which("dotnet")
    if candidate:
        return candidate

    known_paths = (
        "/usr/bin/dotnet",
        "/usr/local/bin/dotnet",
        "/snap/bin/dotnet",
        "/usr/share/dotnet/dotnet",
    )
    for path in known_paths:
        if os.path.exists(path):
            return path

    return None


def main() -> int:
    deploy_dir = Path(__file__).resolve().parent
    repo_root = deploy_dir.parent
    project = deploy_dir / "ArgusEngine.DeployUi" / "ArgusEngine.DeployUi.csproj"

    dotnet = resolve_dotnet()
    if dotnet:
        command = [dotnet, "run", "--project", str(project), "--", *sys.argv[1:]]

        if hasattr(os, "execvp"):
            os.chdir(repo_root)
            os.execvp(dotnet, command)
            return 1

        return subprocess.call(command, cwd=repo_root)

    fallback = ["bash", str(deploy_dir / "deploy.sh"), *sys.argv[1:]]
    env = dict(os.environ)
    env["ARGUS_NO_UI"] = "1"
    sys.stderr.write(
        "argus deploy-ui: dotnet runtime was not found. "
        "Falling back to deploy.sh non-UI mode.\n"
    )

    if hasattr(os, "execvpe"):
        os.chdir(repo_root)
        os.execvpe("bash", fallback, env)
        return 1

    return subprocess.call(fallback, cwd=repo_root, env=env)


if __name__ == "__main__":
    raise SystemExit(main())
