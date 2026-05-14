#!/usr/bin/env bash

# lib-fast-hot-swap.sh — parallel host-native hot-swap for all changed services.
#
# Called from argus_compose_deploy_all when mode=hot and only source files changed.
# Publishes all changed .NET services in parallel on the host (using the SDK if
# available, or a shared SDK container), then simultaneously copies outputs into
# running containers and restarts them. Target wall-time for a source-only change:
# < 30s.
#
# Requires: ROOT, argus_docker(), compose() from lib-argus-compose.sh already sourced.

# ── Detect whether dotnet is available on the host ─────────────────────────
argus_host_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    dotnet "$@"
  else
    # Fall back: run inside a shared SDK container that mounts the workspace.
    local uid gid
    uid="$(id -u)"
    gid="$(id -g)"

    argus_docker run --rm \
      --user "$uid:$gid" \
      -v "$ROOT:/workspace" \
      -v "$ROOT/.nuget/packages:/root/.nuget/packages" \
      -w /workspace \
      -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
      -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
      -e NUGET_PACKAGES=/root/.nuget/packages \
      mcr.microsoft.com/dotnet/sdk:10.0 \
      dotnet "$@"
  fi
}

# ── Parallel publish of a list of services ──────────────────────────────────
# Writes per-service log under deploy/logs/hot-swap/.
# Emits structured status lines to stdout: ARGUS_STATUS:<service>:<ok|fail>.
argus_parallel_publish() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  local nuget_dir="$ROOT/.nuget/packages"
  local hot_swap_log_dir="$ROOT/deploy/logs/hot-swap"
  mkdir -p "$nuget_dir"
  mkdir -p "$hot_swap_log_dir"

  # Staging root — all build intermediates and outputs go here, never inside src/.
  local staging_root="$ROOT/deploy/.hot-publish"
  local pids=()

  for service in "${services[@]}"; do
    local csproj out_dir obj_dir log_file
    csproj="$ROOT/$(argus_service_csproj "$service")"
    out_dir="$staging_root/$service/publish"
    obj_dir="$staging_root/$service/obj"
    log_file="$hot_swap_log_dir/argus-publish-${service}.log"

    rm -rf "$staging_root/$service"
    mkdir -p "$out_dir" "$obj_dir"

    # Relative paths used inside the Docker container (workspace = $ROOT).
    local rel_csproj rel_out rel_obj
    rel_csproj="$(realpath --relative-to="$ROOT" "$csproj")"
    rel_out="deploy/.hot-publish/$service/publish"
    rel_obj="deploy/.hot-publish/$service/obj"

    (
      set +e

      # MSBuild properties that redirect ALL build artifacts outside src/.
      local msbuild_redirects
      msbuild_redirects=(
        "/p:UseAppHost=false"
        "/p:BaseIntermediateOutputPath=$obj_dir/"
        "/p:BaseOutputPath=$out_dir/../bin/"
        "-p:maxcpucount"
      )

      if command -v dotnet >/dev/null 2>&1; then
        # Restore into the same redirected obj directory used by publish.
        # Without this, publish --no-restore can fail with NETSDK1004 because
        # project.assets.json was generated in the default src/<project>/obj.
        NUGET_PACKAGES="$nuget_dir" \
          dotnet restore "$csproj" \
          "${msbuild_redirects[@]}" \
          >"$log_file" 2>&1 && \
        NUGET_PACKAGES="$nuget_dir" \
          dotnet publish "$csproj" \
          -c Release \
          -o "$out_dir" \
          --no-restore \
          "${msbuild_redirects[@]}" \
          >>"$log_file" 2>&1
      else
        local uid gid
        uid="$(id -u)"
        gid="$(id -g)"

        argus_docker run --rm \
          --user "$uid:$gid" \
          -v "$ROOT:/workspace" \
          -v "$nuget_dir:/nuget" \
          -w /workspace \
          -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
          -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
          -e NUGET_PACKAGES=/nuget \
          mcr.microsoft.com/dotnet/sdk:10.0 \
          sh -c 'dotnet restore "$1" \
                   "/p:UseAppHost=false" \
                   "/p:BaseIntermediateOutputPath=$2/" \
                   "/p:BaseOutputPath=$2/../bin/" \
                   "-p:maxcpucount" && \
                 dotnet publish "$1" \
                   -c Release \
                   -o "$3" \
                   --no-restore \
                   "/p:UseAppHost=false" \
                   "/p:BaseIntermediateOutputPath=$2/" \
                   "/p:BaseOutputPath=$2/../bin/" \
                   "-p:maxcpucount"' \
          sh "$rel_csproj" "$rel_obj" "$rel_out" \
          >"$log_file" 2>&1
      fi

      local rc=$?
      if [[ $rc -eq 0 ]]; then
        echo "ARGUS_STATUS:${service}:ok"
      else
        echo "ARGUS_STATUS:${service}:fail"
        echo "=== BUILD FAILED: $service ===" >&2
        tail -30 "$log_file" >&2
      fi
    ) &

    pids+=("$!")
    echo "ARGUS_PUBLISH_START:${service}"
  done

  for pid in "${pids[@]}"; do
    wait "$pid"
  done

  return 0
}

# ── Copy publish output into running containers in parallel ─────────────────
# Reads from deploy/.hot-publish/<service>/publish/ (never from src/).
argus_parallel_hot_copy_and_restart_from_staging() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  local copy_pids=()

  for service in "${services[@]}"; do
    local out_abs="$ROOT/deploy/.hot-publish/$service/publish"
    [[ -d "$out_abs" ]] || { echo "WARN: No publish output for $service, skipping copy."; continue; }

    (
      set +e

      local cids running_cids=()
      mapfile -t cids < <(compose ps -q "$service" 2>/dev/null || true)

      for cid in "${cids[@]}"; do
        [[ -n "$cid" ]] || continue

        local running
        running="$(argus_docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null || echo false)"
        [[ "$running" == "true" ]] && running_cids+=("$cid")
      done

      if [[ ${#running_cids[@]} -eq 0 ]]; then
        echo "ARGUS_COPY_SKIP:${service}:no-running-container"
        exit 0
      fi

      local copy_sub_pids=()
      for cid in "${running_cids[@]}"; do
        local temp="/tmp/argus-hot-${service}-${cid:0:8}"

        (
          argus_docker exec "$cid" sh -c "rm -rf '$temp' && mkdir -p '$temp'" 2>/dev/null
          argus_docker cp "$out_abs/." "$cid:$temp/" 2>/dev/null
          argus_docker exec "$cid" sh -c "find /app -mindepth 1 -maxdepth 1 -exec rm -rf {} + && cp -a '$temp'/. /app/ && rm -rf '$temp'" 2>/dev/null
        ) &

        copy_sub_pids+=("$!")
      done

      for p in "${copy_sub_pids[@]}"; do
        wait "$p"
      done

      echo "ARGUS_COPY_DONE:${service}"
    ) &

    copy_pids+=("$!")
  done

  for p in "${copy_pids[@]}"; do
    wait "$p"
  done

  echo "ARGUS_ALL_COPIES_DONE"

  local restart_pids=()
  for service in "${services[@]}"; do
    ( compose restart "$service" >/dev/null 2>&1 && echo "ARGUS_RESTART_DONE:${service}" ) &
    restart_pids+=("$!")
  done

  for p in "${restart_pids[@]}"; do
    wait "$p"
  done

  echo "ARGUS_ALL_RESTARTS_DONE"
}

argus_note_hot_swapped_services() {
  local hot=("$@")
  [[ ${#hot[@]} -gt 0 ]] || return 0

  # Keep this separate from argus_BUILT_SERVICES. A hot-swap updates running
  # containers only; it does not update the named Docker image. Leaving image
  # build fingerprints stale allows later force-recreate/runtime-config paths to
  # materialize a real image before Compose creates a new container from it.
  # shellcheck disable=SC2206
  local previous=( ${argus_HOT_SWAPPED_SERVICES:-} )
  argus_HOT_SWAPPED_SERVICES="$(argus_unique_services "${previous[@]}" "${hot[@]}")"
  export argus_HOT_SWAPPED_SERVICES
}

# ── Entry point called from argus_compose_hot_deploy ────────────────────────
# Usage: argus_fast_hot_swap service1 service2 ...
# Returns 0 on success. Falls back to image rebuild list on failure.
argus_fast_hot_swap() {
  local services=("$@")
  [[ ${#services[@]} -gt 0 ]] || return 0

  echo "ARGUS_FAST_HOT_SWAP_START:${services[*]}"

  # ── Step 1: Restore (once, shared) ────────────────────────────────────────
  # Run a single dotnet restore for all changed projects using the solution
  # file, so the NuGet cache is warm before parallel publish.
  # Only needed if NuGet cache is cold (first deploy or package change).
  local nuget_dir="$ROOT/.nuget/packages"
  mkdir -p "$nuget_dir"

  local need_restore=0
  if [[ ! -d "$nuget_dir" ]] || [[ "$(ls -A "$nuget_dir" 2>/dev/null | wc -l)" -lt 10 ]]; then
    need_restore=1
  fi

  if [[ "$need_restore" == "1" ]]; then
    echo "ARGUS_PHASE:restore"
    echo "NuGet package cache is cold — running dotnet restore for all projects before parallel publish…"

    if command -v dotnet >/dev/null 2>&1; then
      dotnet restore "$ROOT/ArgusEngine.slnx" --packages "$nuget_dir" 2>&1 |
        grep -E '^  Restored|error|Error' || true
    else
      local uid gid
      uid="$(id -u)"
      gid="$(id -g)"

      argus_docker run --rm \
        --user "$uid:$gid" \
        -v "$ROOT:/workspace" \
        -v "$nuget_dir:/root/.nuget/packages" \
        -w /workspace \
        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        -e NUGET_PACKAGES=/root/.nuget/packages \
        mcr.microsoft.com/dotnet/sdk:10.0 \
        dotnet restore ArgusEngine.slnx --packages /root/.nuget/packages 2>&1 |
        grep -E '^  Restored|error|Error' || true
    fi
  fi

  # ── Step 2: Parallel publish ───────────────────────────────────────────────
  echo "ARGUS_PHASE:publish"
  echo "Starting parallel publish for ${#services[@]} service(s): ${services[*]}"
  argus_parallel_publish "${services[@]}"

  # Check which succeeded.
  local ok_services=()
  local fail_services=()

  for service in "${services[@]}"; do
    local out dll
    out="$ROOT/deploy/.hot-publish/$service/publish"
    dll="$(argus_service_app_dll "$service" 2>/dev/null || true)"

    if [[ -n "$dll" && -f "$out/$dll" ]]; then
      ok_services+=("$service")
    else
      fail_services+=("$service")
      echo "ARGUS_PUBLISH_FAIL:${service}"
    fi
  done

  if [[ ${#fail_services[@]} -gt 0 ]]; then
    echo "WARN: Publish failed for: ${fail_services[*]}; these will be rebuilt as images." >&2
    # Promote failures to image rebuild.
    argus_IMAGE_REBUILD_SERVICES="$(argus_unique_services ${argus_IMAGE_REBUILD_SERVICES:-} "${fail_services[@]}")"
    export argus_IMAGE_REBUILD_SERVICES
  fi

  # ── Step 3: Parallel copy + restart ───────────────────────────────────────
  if [[ ${#ok_services[@]} -gt 0 ]]; then
    echo "ARGUS_PHASE:copy_restart"
    argus_parallel_hot_copy_and_restart_from_staging "${ok_services[@]}"
    argus_note_hot_swapped_services "${ok_services[@]}"
  fi

  echo "ARGUS_FAST_HOT_SWAP_DONE"
  return 0
}
