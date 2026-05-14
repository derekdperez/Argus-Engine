#!/usr/bin/env bash
# Shared service metadata for deployment helpers.
#
# The catalog is intentionally TSV so Bash, Python, and CI can read the same
# source of truth without requiring jq/yq.

argus_catalog_file() {
  local script_dir
  script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
  printf '%s\n' "${ARGUS_SERVICE_CATALOG_FILE:-${script_dir}/service-catalog.tsv}"
}

argus_normalize_service() {
  case "${1:-}" in
    # Backwards-compatible alias for the pre-split Command Center image.
    command-center) printf '%s\n' "command-center-gateway" ;;
    *) printf '%s\n' "$1" ;;
  esac
}

argus_catalog_field() {
  local requested service field catalog
  requested="${1:?service required}"
  field="${2:?field number required}"
  service="$(argus_normalize_service "$requested")"
  catalog="$(argus_catalog_file)"

  awk -F '\t' -v svc="$service" -v field="$field" '
    $0 !~ /^[[:space:]]*#/ && NF >= 6 && $1 == svc { print $field; found=1; exit }
    END { if (!found) exit 1 }
  ' "$catalog"
}

argus_catalog_services() {
  local ecr_only="${1:-0}"
  local catalog
  catalog="$(argus_catalog_file)"

  awk -F '\t' -v ecr_only="$ecr_only" '
    $0 ~ /^[[:space:]]*#/ || NF < 6 { next }
    ecr_only == "1" && $5 != "1" { next }
    { print $1 }
  ' "$catalog"
}

argus_all_catalog_services() {
  argus_catalog_services 0
}

argus_ecr_default_services() {
  argus_catalog_services 1
}

argus_service_project_dir() {
  argus_catalog_field "$1" 2
}

argus_service_project_path() {
  printf 'src/%s\n' "$(argus_service_project_dir "$1")"
}

argus_service_app_dll() {
  argus_catalog_field "$1" 3
}

argus_service_dockerfile() {
  argus_catalog_field "$1" 4
}

argus_service_ecr_enabled() {
  argus_catalog_field "$1" 5
}

argus_service_kind() {
  argus_catalog_field "$1" 6
}

argus_service_extra_source_dirs() {
  argus_catalog_field "$1" 7 2>/dev/null | tr ',' '\n' | awk 'NF { print }'
}

argus_validate_service() {
  local service
  service="$(argus_normalize_service "$1")"
  if ! argus_catalog_field "$service" 1 >/dev/null 2>&1; then
    echo "Unknown service: $1" >&2
    return 1
  fi
  printf '%s\n' "$service"
}
