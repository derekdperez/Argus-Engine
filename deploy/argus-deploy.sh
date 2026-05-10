#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/.." && pwd)"
project="${script_dir}/ArgusEngine.DeployUi/ArgusEngine.DeployUi.csproj"

cd "$repo_root"
exec dotnet run --project "$project" -- "$@"
