#!/usr/bin/env bash
# Lightweight checks for diagnosing a stuck Argus local/EC2 deploy.
set -Eeuo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

echo "== Argus deploy preflight =="
echo "repo: $ROOT"
echo ""

echo "== Git state =="
if [[ -d .git ]]; then
  git --no-pager status --short || true
  git rev-parse --short HEAD || true
else
  echo "not a git checkout"
fi
echo ""

echo "== Docker =="
docker version --format 'client={{.Client.Version}} server={{.Server.Version}}' 2>/dev/null || docker version || true
docker compose version 2>/dev/null || docker-compose version 2>/dev/null || true
echo ""

echo "== Docker disk usage =="
docker system df || true
echo ""

echo "== Compose services =="
docker compose -f deploy/docker-compose.yml config --services 2>/dev/null || true
echo ""

echo "== Recent build/deploy logs =="
ls -1t deploy/logs/deploy_summary_*.log 2>/dev/null | head -5 || true
echo ""

echo "== Currently running build/container processes =="
ps -eo pid,ppid,pcpu,pmem,etime,cmd | grep -E 'docker|buildkit|dotnet|deploy-ui|deploy.sh' | grep -v grep || true
echo ""

cat <<'EOF'
Recommended next command for a stalled UI build:

  ./deploy/deploy-batched.sh --image

If Docker permissions require sudo:

  sudo ./deploy/deploy-batched.sh --image

EOF
