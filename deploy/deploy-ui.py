#!/usr/bin/env python3
"""Compatibility shim.

The deployment UI has moved to the .NET console project at
deploy/ArgusEngine.DeployUi. This file remains only because older deploy.sh
versions execute deploy-ui.py directly when running in an interactive terminal.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path


def main() -> int:
    deploy_dir = Path(__file__).resolve().parent
    repo_root = deploy_dir.parent
    project = deploy_dir / "ArgusEngine.DeployUi" / "ArgusEngine.DeployUi.csproj"

    command = ["dotnet", "run", "--project", str(project), "--", *sys.argv[1:]]

    if hasattr(os, "execvp"):
        os.chdir(repo_root)
        os.execvp("dotnet", command)
        return 1

    return subprocess.call(command, cwd=repo_root)


if __name__ == "__main__":
    raise SystemExit(main())
