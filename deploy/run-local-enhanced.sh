#!/usr/bin/env bash
# Enhanced local deployment script for NightmareV2 with comprehensive status reporting
# and Codespace public URL generation.
#
# This script can be launched from VS Code debugger in GitHub Codespace.
#
# Usage:
#   ./deploy/run-local-enhanced.sh              # Deploy with incremental build
#   ./deploy/run-local-enhanced.sh -fresh       # Fresh build with --no-cache
#   ./deploy/run-local-enhanced.sh down         # Stop and remove containers
#   ./deploy/run-local-enhanced.sh logs         # Follow logs
#   ./deploy/run-local-enhanced.sh status       # Show status only
#
# Features:
#   - Automatic Docker installation on Linux
#   - 10 HTTP request workers per deployment
#   - Health check polling with timeout
#   - Formatted status report with port mappings
#   - Public Codespace URL generation
#   - Service dependency validation

set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$ROOT"

# Color codes for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Configuration
DEPLOYMENT_TIMEOUT=300  # 5 minutes for services to become healthy
HEALTH_CHECK_INTERVAL=5 # Check every 5 seconds
MAX_RETRIES=$((DEPLOYMENT_TIMEOUT / HEALTH_CHECK_INTERVAL))

# Logging functions
log_section() {
  echo -e "\n${BLUE}════════════════════════════════════════════════════════════${NC}"
  echo -e "${BLUE}  $1${NC}"
  echo -e "${BLUE}════════════════════════════════════════════════════════════${NC}\n"
}

log_info() {
  echo -e "${GREEN}ℹ${NC} $1"
}

log_status() {
  echo -e "${GREEN}✓${NC} $1"
}

log_warn() {
  echo -e "${YELLOW}⚠${NC} $1"
}

log_error() {
  echo -e "${RED}✗${NC} $1"
}

# Detect if running in Codespace
detect_codespace() {
  if [[ -n "${CODESPACES:-}" ]] && [[ "${CODESPACES}" == "true" ]]; then
    return 0
  fi
  return 1
}

# Get Codespace hostname
get_codespace_url() {
  if [[ -n "${CODESPACE_NAME:-}" ]]; then
    local domain="${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-github.dev}"
    echo "https://${CODESPACE_NAME}-8080.${domain}"
  else
    echo "http://localhost:8080"
  fi
}

# Check if port is accessible
wait_for_port() {
  local port=$1
  local timeout=$2
  local service=$3
  local retries=$((timeout / HEALTH_CHECK_INTERVAL))
  
  log_info "Waiting for $service on port $port..."
  
  for ((i=0; i<retries; i++)); do
    if nc -z localhost "$port" 2>/dev/null; then
      return 0
    fi
    echo -n "."
    sleep "$HEALTH_CHECK_INTERVAL"
  done
  
  echo ""
  return 1
}

# Check service health via HTTP
wait_for_http_health() {
  local url=$1
  local timeout=$2
  local service=$3
  local retries=$((timeout / HEALTH_CHECK_INTERVAL))
  
  log_info "Health checking $service at $url..."
  
  for ((i=0; i<retries; i++)); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      return 0
    fi
    echo -n "."
    sleep "$HEALTH_CHECK_INTERVAL"
  done
  
  echo ""
  return 1
}

# Show deployment status
show_status() {
  log_section "Deployment Status"
  
  # Check if containers are running
  local running_count=$(docker compose -f deploy/docker-compose.yml ps --services --filter status=running 2>/dev/null | wc -l)
  local total_count=$(docker compose -f deploy/docker-compose.yml config --services 2>/dev/null | wc -l)
  
  if [[ $running_count -eq 0 ]]; then
    log_warn "No containers running. Deploy with: ./deploy/run-local.sh"
    return 1
  fi
  
  echo ""
  log_info "Container Status:"
  docker compose -f deploy/docker-compose.yml ps --no-trunc | tail -n +2 | awk '{
    printf "  • %-25s %-20s %s\n", $1, $2, $3
  }'
  
  echo ""
  log_info "Running: $running_count/$total_count services"
  
  # Check specific service health
  echo ""
  log_info "Service Health:"
  
  if nc -z localhost 8080 2>/dev/null; then
    log_status "Command Center (HTTP port 8080)"
  else
    log_warn "Command Center not responding on port 8080"
  fi
  
  if nc -z localhost 5432 2>/dev/null; then
    log_status "PostgreSQL (port 5432)"
  else
    log_warn "PostgreSQL not responding"
  fi
  
  if nc -z localhost 6379 2>/dev/null; then
    log_status "Redis (port 6379)"
  else
    log_warn "Redis not responding"
  fi
  
  if nc -z localhost 5672 2>/dev/null; then
    log_status "RabbitMQ (port 5672)"
  else
    log_warn "RabbitMQ not responding"
  fi
  
  # Count worker replicas
  local worker_count=$(docker ps -f "label=com.docker.compose.service=worker-spider" --quiet 2>/dev/null | wc -l)
  if [[ $worker_count -gt 0 ]]; then
    log_status "HTTP Workers: $worker_count containers"
  fi
  
  return 0
}

# Show access information
show_access_info() {
  log_section "Access Information"
  
  local url=$(get_codespace_url)
  
  if detect_codespace; then
    log_status "GitHub Codespace Detected"
    echo ""
    echo -e "  ${GREEN}Web Interface:${NC}"
    echo -e "    ${BLUE}$url${NC}"
    echo ""
    log_info "This URL is publicly accessible from your browser"
  else
    log_info "Local Deployment"
    echo ""
    echo -e "  ${GREEN}Web Interface:${NC}"
    echo -e "    ${BLUE}http://localhost:8080${NC}"
  fi
  
  echo ""
  echo -e "  ${GREEN}Ports:${NC}"
  echo "    • 8080 - Command Center Web UI"
  echo "    • 5432 - PostgreSQL"
  echo "    • 6379 - Redis"
  echo "    • 5672 - RabbitMQ (AMQP)"
  echo "    • 15672 - RabbitMQ Management UI"
  echo ""
  echo -e "  ${GREEN}API Endpoints:${NC}"
  echo "    • GET  $url/health/ready"
  echo "    • GET  $url/api/http-request-queue/metrics"
  echo "    • GET  $url/api/ops/docker-status"
}

# Show helpful commands
show_commands() {
  log_section "Useful Commands"
  
  echo -e "  ${GREEN}View logs:${NC}"
  echo "    ./deploy/run-local.sh logs"
  echo ""
  echo -e "  ${GREEN}Check container status:${NC}"
  echo "    docker compose -f deploy/docker-compose.yml ps"
  echo ""
  echo -e "  ${GREEN}View worker logs:${NC}"
  echo "    docker compose -f deploy/docker-compose.yml logs -f worker-spider"
  echo ""
  echo -e "  ${GREEN}Check HTTP queue metrics:${NC}"
  echo "    curl http://localhost:8080/api/http-request-queue/metrics | jq"
  echo ""
  echo -e "  ${GREEN}Stop deployment:${NC}"
  echo "    ./deploy/run-local.sh down"
  echo ""
  echo -e "  ${GREEN}Fresh rebuild:${NC}"
  echo "    ./deploy/run-local.sh -fresh"
}

# Main deployment flow
deploy() {
  log_section "NightmareV2 Local Deployment"
  
  # Source the main run-local script logic to handle all setup
  if [[ ! -f "$DEPLOY_DIR/run-local.sh" ]]; then
    log_error "run-local.sh not found at $DEPLOY_DIR/run-local.sh"
    exit 1
  fi
  
  # Export command to pass to run-local.sh
  export CMD="${1:-up}"
  
  # Source and run the core deployment script
  # shellcheck source=deploy/run-local.sh
  source "$DEPLOY_DIR/run-local.sh"
  
  # Only show status/access info if we deployed (not for down/logs)
  if [[ "${1:-up}" == "up" ]]; then
    # Wait for Command Center to be healthy
    if ! wait_for_http_health "http://localhost:8080/health/ready" "$DEPLOYMENT_TIMEOUT" "Command Center"; then
      log_warn "Command Center health check timed out (may still be starting)"
    else
      log_status "Command Center is healthy"
    fi
    
    echo ""
    sleep 2  # Brief pause for remaining services to stabilize
    
    # Show comprehensive status
    show_status && show_access_info && show_commands
  fi
}

# Parse command line arguments
CMD="${1:-up}"

case "$CMD" in
  up|""|--hot|-hot|--fresh|-fresh|-image|--image)
    deploy "$@"
    ;;
  down)
    log_section "Stopping NightmareV2"
    cd "$ROOT"
    # shellcheck source=deploy/lib-nightmare-compose.sh
    source "$DEPLOY_DIR/lib-nightmare-compose.sh"
    compose down --remove-orphans
    log_status "Deployment stopped"
    ;;
  logs)
    log_section "NightmareV2 Logs"
    cd "$ROOT"
    # shellcheck source=deploy/lib-nightmare-compose.sh
    source "$DEPLOY_DIR/lib-nightmare-compose.sh"
    compose logs -f
    ;;
  status)
    show_status
    ;;
  ps)
    cd "$ROOT"
    # shellcheck source=deploy/lib-nightmare-compose.sh
    source "$DEPLOY_DIR/lib-nightmare-compose.sh"
    compose ps
    ;;
  -h|--help)
    cat <<'EOF'
Enhanced local deployment script for NightmareV2

Usage: ./deploy/run-local-enhanced.sh [COMMAND] [OPTIONS]

Commands:
  up (default)      Deploy with incremental build (skips build if unchanged)
  -fresh            Full rebuild with --no-cache and force-recreate
  --hot             Source-only hot-swap for changed services
  down              Stop and remove all containers
  logs              Follow logs from all services
  ps / status       Show container status
  -h / --help       Show this help message

Environment Variables:
  NIGHTMARE_DEPLOY_FRESH=1      Full rebuild (same as -fresh)
  NIGHTMARE_DEPLOY_MODE=hot     Hot-swap mode (same as --hot)
  NIGHTMARE_GIT_PULL=1          Git pull before build
  NIGHTMARE_SKIP_INSTALL=1      Skip Docker installation checks
  SUBFINDER_PACKAGE=...         Override subfinder package version
  AMASS_PACKAGE=...             Override amass package version

Features:
  • 10 HTTP request workers automatically deployed
  • Comprehensive health checks with timeout handling
  • GitHub Codespace public URL generation
  • Formatted deployment summary and status reporting
  • Helpful command reference

Examples:
  ./deploy/run-local-enhanced.sh              # Normal deploy
  ./deploy/run-local-enhanced.sh -fresh       # Fresh rebuild
  ./deploy/run-local-enhanced.sh logs         # View logs
  NIGHTMARE_GIT_PULL=1 ./deploy/run-local-enhanced.sh  # With git pull

EOF
    ;;
  *)
    log_error "Unknown command: $CMD"
    echo "Use -h or --help for usage information"
    exit 1
    ;;
esac
