import sys, os, subprocess, time, re, threading, queue, signal, shutil, json

if not sys.stdout.isatty():
    deploy_sh = os.path.join(os.path.dirname(os.path.abspath(__file__)), "deploy.sh")
    os.execvp("bash", ["bash", deploy_sh] + sys.argv[1:])

E = "\033"
def mv(r,c):    return f"{E}[{r};{c}H"
def eol():      return f"{E}[K"
def rst():      return f"{E}[0m"
def bold():     return f"{E}[1m"
def dim():      return f"{E}[2m"
def fg(n):      return f"{E}[38;5;{n}m"
def bg(n):      return f"{E}[48;5;{n}m"

C_CYAN   = fg(51);  C_AMBER  = fg(220); C_GREEN  = fg(82)
C_RED    = fg(196); C_GREY   = fg(245); C_DARK   = fg(238)
C_WHITE  = fg(255); C_SEP    = fg(238); C_MUTED  = fg(243)
C_HDR    = bg(234); C_BAR_F  = fg(39);  C_BAR_E  = fg(236)
C_WARN   = fg(214); C_INFO   = fg(75)

SPIN = ["⠋","⠙","⠹","⠸","⠼","⠴","⠦","⠧","⠇","⠏"]
BF, BE = "█", "░"

def termsize():
    s = shutil.get_terminal_size((120, 40))
    return s.lines, s.columns

# ── Phase descriptions shown to user ────────────────────────────────────────
PHASE_DEFS = [
    {
        "id": "init",
        "name": "Initialize",
        "weight": 3, "est": 8,
        "desc": "Reading git state, computing source fingerprints for all services, and deciding what needs to be rebuilt.",
        "tasks": {
            "fingerprint":  "Hashing source files for each service to detect changes since last deploy…",
            "compare":      "Comparing fingerprints against last successful deployment…",
            "plan":         "Building deployment plan: deciding hot-swap vs. image rebuild vs. skip…",
        }
    },
    {
        "id": "build",
        "name": "Build / Publish",
        "weight": 62, "est": 180,
        "desc": "Compiling changed .NET services and producing deployable artifacts. Source-only changes are published directly; Dockerfile changes trigger a full image rebuild via Docker BuildKit.",
        "tasks": {
            "restore":      "Restoring NuGet packages into shared cache…",
            "publish":      "Compiling and publishing changed services in parallel (no Docker build required)…",
            "docker_build": "Running docker compose build for services with Dockerfile/image changes…",
            "copy":         "Copying publish output into running containers…",
            "restart":      "Restarting updated containers to pick up new binaries…",
        }
    },
    {
        "id": "up",
        "name": "Apply Compose Stack",
        "weight": 28, "est": 50,
        "desc": "Running docker compose up to reconcile the desired state: starting any stopped services, applying config changes, and ensuring all containers are healthy.",
        "tasks": {
            "up":           "Bringing all compose services up and waiting for containers to start…",
            "health":       "Waiting for services to become healthy…",
        }
    },
    {
        "id": "verify",
        "name": "Verify & Finalize",
        "weight": 7, "est": 15,
        "desc": "Confirming the deployment succeeded: checking that the Command Center web UI is reachable and Blazor assets are correctly served.",
        "tasks": {
            "blazor":       "Verifying Blazor static assets (blazor.web.js) inside the running container…",
            "http":         "Confirming the web UI responds with HTTP 200 on the expected port…",
            "stamp":        "Saving deployment fingerprints so the next run knows what was last deployed…",
        }
    },
]

METRICS_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".deploy-metrics.json")

def load_metrics():
    try:
        if os.path.exists(METRICS_FILE):
            with open(METRICS_FILE, "r") as f:
                return json.load(f)
    except:
        pass
    return {}

# ── State ────────────────────────────────────────────────────────────────────
class State:
    def __init__(self):
        metrics = load_metrics()
        self.phases = []
        for d in PHASE_DEFS:
            p = {**d, "pct": 0, "status": "pending", "detail": "", "active_task": ""}
            # Override estimate with historical average if available
            if d["id"] in metrics:
                p["est"] = metrics[d["id"]].get("avg", d["est"])
            self.phases.append(p)
            
        self.cur = 0
        self.logs = []          # filtered display logs
        self.all_logs = []      # every raw line (for error dump)
        self.error_lines = []   # lines containing errors
        self.spin_i = 0
        now = time.time()
        self.start = now
        self.phase_start = now
        self.last_activity = now   # watchdog: last time we saw output
        self.stall_warned = False
        # build tracking
        self.build_total = 0;  self.build_done = 0
        self.pub_total   = 0;  self.pub_done   = 0
        self.pub_status  = {}  # svc→status str
        # compose tracking
        self.compose_up   = set()
        self.compose_total = 21
        # events for verbose status
        self.current_event = ""   # short single-line current activity
        self.phase_durations = {} # actual durations recorded

    STALL_WARN_SECS  = 60   # show warning after 60s silence
    STALL_ABORT_SECS = 600  # suggest abort after 10min silence

    def ph(self, pid=None):
        if pid is None: return self.phases[self.cur] if self.cur < len(self.phases) else None
        for p in self.phases:
            if p["id"] == pid: return p
        return None

    def spin(self):
        self.spin_i = (self.spin_i + 1) % len(SPIN)
        return SPIN[self.spin_i]

    def advance(self, pid):
        now = time.time()
        for i, p in enumerate(self.phases):
            if p["status"] == "running":
                p["pct"] = 100; p["status"] = "done"; p["active_task"] = ""
                duration = now - self.phase_start
                self.phase_durations[p["id"]] = duration
            if p["id"] == pid and p["status"] == "pending":
                p["status"] = "running"
                self.cur = i
                self.phase_start = now
                break

    def set_task(self, pid, task_key):
        ph = self.ph(pid)
        if ph: ph["active_task"] = task_key

    def overall(self):
        tw = sum(p["weight"] for p in self.phases)
        dw = sum(p["pct"] * p["weight"] / 100 for p in self.phases)
        return min(100, int(dw * 100 / tw))


# ── Parser ───────────────────────────────────────────────────────────────────
def feed(s: State, raw: str):
    line = raw.rstrip()
    if not line: return
    ll = line.lower()

    s.all_logs.append(line)
    if len(s.all_logs) > 2000: s.all_logs = s.all_logs[-2000:]
    s.last_activity = time.time()
    s.stall_warned = False

    # Track error lines for diagnostics
    if any(x in ll for x in ("error", "fatal", "fail", "cannot", "permission denied", "exception")):
        s.error_lines.append(line)
        if len(s.error_lines) > 200: s.error_lines = s.error_lines[-200:]

    # Skip noise
    if line.startswith("+ ") or line.startswith("++ "): return
    if re.match(r'^#\d+ \[', line): return

    s.logs.append(line)
    if len(s.logs) > 300: s.logs = s.logs[-300:]

    # ── ARGUS structured markers ──────────────────────────────────────────────
    m = re.match(r'^ARGUS_PHASE:(\S+)', line)
    if m:
        pid = m.group(1)
        s.advance(pid)
        if pid == "build":
            s.set_task("build", "docker_build")
        elif pid == "up":
            s.set_task("up", "up")
        elif pid == "copy_restart":
            s.set_task("build", "copy")
        return

    m = re.match(r'^ARGUS_FAST_HOT_SWAP_START:(.+)', line)
    if m:
        svcs = m.group(1).split()
        s.pub_total = len(svcs)
        for sv in svcs: s.pub_status[sv] = "queued"
        s.advance("build"); s.set_task("build", "publish")
        s.current_event = f"Parallel publish starting for {len(svcs)} service(s): {', '.join(svcs[:4])}{'…' if len(svcs)>4 else ''}"
        return

    m = re.match(r'^ARGUS_PUBLISH_START:(\S+)', line)
    if m:
        sv = m.group(1); s.pub_status[sv] = "building"
        s.current_event = f"Compiling {sv}…"
        return

    m = re.match(r'^ARGUS_STATUS:(\S+):(ok|fail)', line)
    if m:
        sv, res = m.group(1), m.group(2)
        s.pub_status[sv] = res
        s.pub_done = sum(1 for v in s.pub_status.values() if v in ("ok","fail"))
        _build_pct(s)
        s.current_event = f"{'✔' if res=='ok' else '✘'} {sv} publish {res}  ({s.pub_done}/{s.pub_total})"
        return

    m = re.match(r'^ARGUS_COPY_DONE:(\S+)', line)
    if m:
        s.set_task("build", "copy")
        s.current_event = f"Copied binaries into {m.group(1)} container"
        return

    m = re.match(r'^ARGUS_ALL_COPIES_DONE', line)
    if m:
        s.set_task("build", "restart")
        s.current_event = "All containers updated — restarting services…"
        return

    m = re.match(r'^ARGUS_RESTART_DONE:(\S+)', line)
    if m:
        s.current_event = f"✔ {m.group(1)} restarted"
        return

    m = re.match(r'^ARGUS_ALL_RESTARTS_DONE', line)
    if m:
        s.advance("up"); s.set_task("up", "up")
        s.current_event = "All services restarted — reconciling compose stack…"
        return

    # ── Init phase ────────────────────────────────────────────────────────────
    if s.cur == 0:
        ph = s.ph("init"); ph["status"] = "running"
        s.set_task("init", "fingerprint")
        if "build_source_stamp" in ll:
            ph["detail"] = line[:80]
            s.current_event = f"Build stamp computed: {line.split('=',1)[-1][:40]}"
        elif "hot deploy plan" in ll:
            s.set_task("init", "plan")
            s.current_event = "Deployment plan ready — inspecting what changed…"
        elif "image rebuild service" in ll:
            m2 = re.search(r'image rebuild service.*?:\s*(.+)', line, re.I)
            if m2:
                svcs = m2.group(1).split(); s.build_total = len(svcs)
                for sv in svcs: s.pub_status.setdefault(sv, "pending")
            s.current_event = f"Will rebuild {s.build_total} service image(s)"
        elif "hot-swap service" in ll:
            s.current_event = f"Source-only change detected — will hot-swap: {line.split(':',1)[-1].strip()[:60]}"
        elif "skip build" in ll or "no unapplied" in ll:
            s.current_event = "No changes detected — skipping build entirely"
            ph["pct"] = 100; ph["status"] = "done"
            bph = s.ph("build"); bph["pct"] = 100; bph["status"] = "skipped"; bph["detail"] = "No source or image changes — build skipped"
            s.advance("up"); s.set_task("up", "up")
            return
        if "image rebuild service" in ll or "hot deploy plan" in ll or "fast deploy" in ll:
            ph["pct"] = 100; ph["status"] = "done"
            s.advance("build")
        elif ph["pct"] < 85:
            ph["pct"] = min(85, ph["pct"] + 12)
        ph["detail"] = s.current_event
        return

    # ── Build phase ───────────────────────────────────────────────────────────
    if s.ph("build") and s.ph("build")["status"] == "running":
        ph = s.ph("build")
        # restore step
        if "determining projects to restore" in ll or "restoring" in ll:
            s.set_task("build", "restore")
            s.current_event = "Restoring NuGet dependencies…"
        # docker build step lines
        m3 = re.match(r'^#\d+\s+\[([a-z0-9_-]+)\s+build\s+(\d+)/(\d+)\]', line, re.I)
        if m3:
            sv, step, total = m3.group(1).replace("_","-"), int(m3.group(2)), int(m3.group(3))
            s.pub_status[sv] = f"{step}/{total}"
            s.set_task("build", "docker_build")
            s.current_event = f"docker build  {sv}  step {step}/{total}"
            _build_pct(s)
        # BuildKit service done
        if "exporting" in ll or "writing image" in ll:
            m4 = re.match(r'^#\d+\s+\[([a-z0-9_-]+)\s+(?:final|export)', line, re.I)
            if m4:
                sv = m4.group(1).replace("_","-")
                if s.pub_status.get(sv) != "ok":
                    s.pub_status[sv] = "ok"; s.build_done += 1
                s.current_event = f"✔ Image built: {sv}  ({s.build_done}/{s.build_total or '?'})"
                _build_pct(s)
        # Transition to up
        if any(x in ll for x in ["running network argus", "creating argus-engine", "starting argus-engine"]):
            ph["pct"] = 100; ph["status"] = "done"
            s.advance("up"); s.set_task("up", "up")
        ph["detail"] = s.current_event
        return

    # ── Compose-up phase ──────────────────────────────────────────────────────
    if s.ph("up") and s.ph("up")["status"] == "running":
        ph = s.ph("up")
        m5 = re.search(r'container\s+(argus-engine-[a-z0-9_-]+-\d+)\s+(started|healthy|running|created)', ll)
        if m5:
            cname = m5.group(1); s.compose_up.add(cname)
            s.compose_total = max(s.compose_total, len(s.compose_up) + 1)
            ph["pct"] = max(ph["pct"], min(95, int(len(s.compose_up) / s.compose_total * 95)))
            s.current_event = f"Container up: {cname.replace('argus-engine-','')}  ({len(s.compose_up)}/{s.compose_total})"
            s.set_task("up", "health")
            ph["detail"] = f"{len(s.compose_up)} / {s.compose_total} containers running"
        if any(x in ll for x in ["argus v2 is running", "useful commands", "command center gateway"]):
            ph["pct"] = 100; ph["status"] = "done"
            s.advance("verify"); s.set_task("verify", "blazor")
            s.current_event = "All containers up — running post-deploy verification…"
        return

    # ── Verify phase ─────────────────────────────────────────────────────────
    if s.ph("verify") and s.ph("verify")["status"] == "running":
        ph = s.ph("verify")
        if "blazor static asset" in ll or "blazor.web.js" in ll:
            s.set_task("verify", "blazor"); s.current_event = line[:90]
        elif "fingerprint" in ll or "last deploy" in ll or "commit" in ll:
            s.set_task("verify", "stamp"); s.current_event = "Saving deployment fingerprints…"
        elif "http 200" in ll or "served" in ll or "passed" in ll:
            s.set_task("verify", "http"); s.current_event = f"✔ {line[:80]}"
        else:
            s.current_event = line[:90]
        ph["detail"] = s.current_event
        ph["pct"] = min(95, ph["pct"] + 20)


def _build_pct(s: State):
    ph = s.ph("build")
    if not ph or ph["status"] != "running": return
    total = max(s.build_total, s.pub_total, 1)
    done  = max(s.build_done, s.pub_done)
    ph["pct"] = max(ph["pct"], min(95, int(done / total * 95)))


def time_nudge(s: State):
    ph = s.ph()
    if not ph or ph["status"] != "running": return
    elapsed = time.time() - s.phase_start
    target  = min(95, int(95 * (1 - 2 ** (-elapsed / max(ph["est"], 1) * 1.5))))
    if target > ph["pct"]: ph["pct"] = target


# ── Renderer ─────────────────────────────────────────────────────────────────
def render(s: State, final=False, rc=None):
    rows, cols = termsize()
    out = []
    W = cols

    def wr(row, text):
        out.append(f"{mv(row,1)}{text}{eol()}")

    def hbar(row, ch="─"):
        out.append(f"{mv(row,1)}{C_SEP}{ch*W}{rst()}")

    # ── Stall detection ───────────────────────────────────────────────────────
    idle_secs = time.time() - s.last_activity if not final else 0
    is_stalled = idle_secs >= State.STALL_WARN_SECS

    # ── Header ────────────────────────────────────────────────────────────────
    title = " ARGUS ENGINE  ▸  DEPLOYMENT "
    pad   = max(0, (W - len(title)) // 2)
    wr(1, f"{C_HDR}{C_CYAN}{bold()}{' '*pad}{title}{' '*(W-pad-len(title))}{rst()}")
    
    elapsed = time.time() - s.start
    mins, secs = divmod(int(elapsed), 60)
    
    # Calculate estimated total time based on historical metrics
    est_total = sum(p["est"] for p in s.phases if p["status"] != "skipped")
    est_mins, est_secs = divmod(int(est_total), 60)
    
    mode = " ".join(sys.argv[1:]) or "hot-deploy"
    wr(2, f"  {dim()}{C_GREY}Elapsed: {mins:02d}:{secs:02d} / Est: {est_mins:02d}:{est_secs:02d}   Mode: {mode}{rst()}")
    hbar(3)

    row = 4
    BAR_W = max(20, W - 48)
    sp = s.spin() if not final else " "

    # ── Phase bars ────────────────────────────────────────────────────────────
    for ph in s.phases:
        pct    = ph["pct"]
        status = ph["status"]
        if   status == "done":    icon=f"{C_GREEN}✔{rst()}"; nc=C_GREEN
        elif status == "skipped": icon=f"{C_GREY}–{rst()}"; nc=C_GREY
        elif status == "running": icon=f"{C_AMBER}{sp}{rst()}"; nc=f"{C_AMBER}{bold()}"
        elif status == "failed":  icon=f"{C_RED}✘{rst()}";  nc=C_RED
        else:                     icon=f"{C_DARK}·{rst()}"; nc=C_DARK

        filled = int(pct / 100 * BAR_W)
        bar    = f"{C_BAR_F}{BF*filled}{C_BAR_E}{BE*(BAR_W-filled)}{rst()}"
        wr(row, f"  {icon}  {nc}{ph['name']:<22}{rst()}  {bar}  {bold()}{pct:3d}%{rst()}")
        row += 1

        # Description shown only when running
        if status == "running" and row < rows - 6:
            wr(row, f"       {dim()}{C_INFO}{ph['desc'][:W-10]}{rst()}")
            row += 1

        # Active sub-task
        task_key = ph.get("active_task","")
        task_txt = ph.get("tasks",{}).get(task_key,"")
        if task_txt and status == "running" and row < rows - 5:
            wr(row, f"       {C_WARN}→ {task_txt[:W-12]}{rst()}")
            row += 1

        # Build/publish sub-counters
        if ph["id"] == "build" and status == "running" and row < rows - 4:
            pt = s.pub_total or s.build_total
            pd = s.pub_done  or s.build_done
            if pt:
                bar_s = max(0, W - 40)
                sf = int(pd / pt * bar_s)
                sub_bar = f"{C_GREEN}{BF*sf}{C_BAR_E}{BE*(bar_s-sf)}{rst()}"
                wr(row, f"       {C_MUTED}Services: {pd}/{pt}  {sub_bar}{rst()}")
                row += 1

        # Compose-up counter
        if ph["id"] == "up" and status == "running" and s.compose_up and row < rows - 4:
            ct = s.compose_total; cd = len(s.compose_up)
            bar_s = max(0, W - 40)
            sf = int(cd / ct * bar_s)
            sub_bar = f"{C_GREEN}{BF*sf}{C_BAR_E}{BE*(bar_s-sf)}{rst()}"
            wr(row, f"       {C_MUTED}Containers: {cd}/{ct}  {sub_bar}{rst()}")
            row += 1

    hbar(row); row += 1

    # ── Overall bar ───────────────────────────────────────────────────────────
    ov = s.overall()
    OW = W - 18
    of = int(ov / 100 * OW)
    wr(row, f"  {C_WHITE}{bold()}Overall  {rst()}{C_GREEN}{BF*of}{C_BAR_E}{BE*(OW-of)}{rst()}  {bold()}{ov:3d}%{rst()}")
    row += 1

    # ── Heartbeat + current event banner ──────────────────────────────────────
    hbar(row, "─"); row += 1
    if is_stalled:
        idle_m, idle_s = divmod(int(idle_secs), 60)
        if idle_secs >= State.STALL_ABORT_SECS:
            wr(row, f"  {C_RED}{bold()}⚠ STALL DETECTED: no output for {idle_m}m{idle_s}s — process may be hung. Press Ctrl+C to abort.{rst()}")
        else:
            wr(row, f"  {C_WARN}{bold()}⏳ Waiting for output… ({idle_m}m{idle_s}s since last activity){rst()}")
        row += 1
    elif s.current_event:
        # Show last activity age when > 5s so user knows it's alive
        idle_tag = ""
        if idle_secs > 5:
            idle_tag = f"  {dim()}{C_GREY}({int(idle_secs)}s ago){rst()}"
        ev = s.current_event[:W-20]
        wr(row, f"  {C_AMBER}{bold()}▶ {ev}{rst()}{idle_tag}")
        row += 1
    else:
        wr(row, f"  {C_MUTED}{sp} Waiting for deployment output…{rst()}")
        row += 1

    # ── Final result ──────────────────────────────────────────────────────────
    if final:
        hbar(row, "═"); row += 1
        if rc == 0:
            wr(row, f"  {C_GREEN}{bold()}✔  Deployment complete  — {mins:02d}:{secs:02d}{rst()}")
        else:
            wr(row, f"  {C_RED}{bold()}✘  Deployment FAILED (exit {rc})  — diagnostic report saved below{rst()}")
        row += 1

    hbar(row); row += 1

    # ── Log pane ──────────────────────────────────────────────────────────────
    log_rows = max(3, rows - row - 1)
    label = "Errors" if final and rc and rc != 0 and s.error_lines else "Recent Output"
    wr(row, f"  {dim()}{C_GREY}{label}{rst()}"); row += 1

    SKIP_RE = re.compile(r'^(ARGUS_|#\d+)')
    if final and rc and rc != 0 and s.error_lines:
        visible = s.error_lines[-log_rows:]
    else:
        visible = [l for l in s.logs if not SKIP_RE.match(l)][-log_rows:]

    for lg in visible:
        if row >= rows: break
        ll2 = lg.lower()
        if any(x in ll2 for x in ("error","fatal","failed","cannot","permission denied","exception")):
            c = C_RED
        elif any(x in ll2 for x in ("warning","warn")):
            c = C_WARN
        elif any(x in ll2 for x in ("done","started","healthy","ok","complete","passed","✔")):
            c = C_GREEN
        elif any(x in ll2 for x in ("building","publishing","restoring","copying")):
            c = C_AMBER
        else:
            c = C_MUTED
        wr(row, f"  {c}{lg[:W-4]}{rst()}"); row += 1

    while row <= rows - 1:
        out.append(f"{mv(row,1)}{eol()}"); row += 1
    if final:
        out.append(f"{mv(rows,1)}{dim()}  Press Enter to exit…{rst()}{eol()}")

    sys.stdout.write("".join(out)); sys.stdout.flush()


# ── Main ──────────────────────────────────────────────────────────────────────
def main():
    deploy_dir = os.path.dirname(os.path.abspath(__file__))
    env = os.environ.copy()
    env["ARGUS_NO_UI"]       = "1"
    env["BUILDKIT_PROGRESS"] = "plain"
    cmd = ["bash", os.path.join(deploy_dir, "deploy.sh")] + sys.argv[1:]
    logQ: queue.Queue = queue.Queue()
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, bufsize=1, env=env,
                            cwd=os.path.dirname(deploy_dir))
    def _read():
        for ln in iter(proc.stdout.readline, ""):
            logQ.put(ln)
        logQ.put(None)
    threading.Thread(target=_read, daemon=True).start()

    state = State()
    sys.stdout.write(f"{E}[?1049h{E}[?25l{E}[2J{E}[H"); sys.stdout.flush()

    def cleanup(sig=None, _f=None):
        sys.stdout.write(f"{E}[?25h{E}[?1049l"); sys.stdout.flush()
        if proc.poll() is None: proc.terminate()
        sys.exit(130)
    signal.signal(signal.SIGINT, cleanup); signal.signal(signal.SIGTERM, cleanup)

    tick = 0; done = False
    try:
        while not done:
            drained = 0
            while drained < 80:
                try:   item = logQ.get_nowait()
                except queue.Empty: break
                if item is None: done = True; break
                feed(state, item); drained += 1
            time_nudge(state)
            if tick % 2 == 0: render(state)
            tick += 1
            if not done: time.sleep(0.1)

        proc.wait()
        rc = proc.returncode
        now = time.time()
        
        # Record final phase duration if it was running
        for p in state.phases:
            if p["status"] == "running":
                state.phase_durations[p["id"]] = now - state.phase_start
                
        if rc == 0:
            for p in state.phases:
                if p["status"] not in ("skipped",): p["status"]="done"; p["pct"]=100
            # Save metrics on success
            _save_metrics(state)
        else:
            for p in state.phases:
                if p["status"] == "running": p["status"] = "failed"

        # Write structured error report for AI agent diagnostics
        if rc != 0:
            _write_error_report(state, rc)

        render(state, final=True, rc=rc)
        try: sys.stdin.readline()
        except Exception: time.sleep(5)
    finally:
        sys.stdout.write(f"{E}[?25h{E}[?1049l"); sys.stdout.flush()

    # After leaving alt-screen, print error report path if failed
    if proc.returncode != 0:
        report = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                              "logs", "deploy_error_report.txt")
        if os.path.isfile(report):
            print(f"\n{C_RED}{bold()}Deployment failed.{rst()}")
            print(f"Structured error report saved to: {C_WARN}{report}{rst()}")
            print(f"Paste the contents of that file to an AI coding agent for diagnosis.\n")
    sys.exit(proc.returncode)


def _write_error_report(state: State, rc: int):
    """Write a structured diagnostic file that can be pasted to an AI agent."""
    report_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")
    os.makedirs(report_dir, exist_ok=True)
    report_path = os.path.join(report_dir, "deploy_error_report.txt")

    elapsed = time.time() - state.start
    mins, secs = divmod(int(elapsed), 60)

    lines = []
    lines.append("="*80)
    lines.append("ARGUS ENGINE — DEPLOYMENT ERROR REPORT")
    lines.append(f"Generated: {time.strftime('%Y-%m-%d %H:%M:%S')}")
    lines.append(f"Exit code: {rc}")
    lines.append(f"Total elapsed: {mins}m {secs}s")
    lines.append(f"Command: {' '.join(sys.argv)}")
    lines.append("="*80)

    lines.append("\n## Phase Summary")
    for ph in state.phases:
        lines.append(f"  [{ph['status']:>8}] {ph['name']:<25} {ph['pct']:3d}%  {ph.get('detail','')}")

    lines.append("\n## Failed Phase Detail")
    for ph in state.phases:
        if ph["status"] == "failed":
            lines.append(f"  Phase: {ph['name']}")
            lines.append(f"  Description: {ph['desc']}")
            task_key = ph.get("active_task", "")
            task_txt = ph.get("tasks", {}).get(task_key, "")
            if task_txt:
                lines.append(f"  Active task at failure: {task_txt}")

    if state.error_lines:
        lines.append(f"\n## Error Lines ({len(state.error_lines)} total)")
        for el in state.error_lines[-50:]:
            lines.append(f"  {el}")

    lines.append(f"\n## Last 80 Lines of Raw Output")
    for al in state.all_logs[-80:]:
        lines.append(f"  {al}")

    lines.append("\n## Build/Publish Service Status")
    if state.pub_status:
        for svc, st in sorted(state.pub_status.items()):
            lines.append(f"  {svc}: {st}")
    else:
        lines.append("  (no service builds tracked)")

    lines.append("\n## Compose Container Status")
    if state.compose_up:
        for c in sorted(state.compose_up):
            lines.append(f"  {c}: started")
        lines.append(f"  Total up: {len(state.compose_up)}/{state.compose_total}")
    else:
        lines.append("  (no containers tracked)")

    lines.append("\n" + "="*80)
    lines.append("To diagnose: paste this entire file to an AI coding agent.")
    lines.append("="*80 + "\n")

    with open(report_path, "w") as f:
        f.write("\n".join(lines))


def _save_metrics(state: State):
    """Save phase durations and update historical averages."""
    metrics = load_metrics()
    
    for pid, dur in state.phase_durations.items():
        if pid not in metrics:
            metrics[pid] = {"avg": dur, "count": 1}
        else:
            # Running average
            count = metrics[pid].get("count", 0)
            current_avg = metrics[pid].get("avg", 0.0)
            
            # Simple moving average or exponential? Let's use a simple moving average capped at N
            new_count = min(count + 1, 10) # Keep weight of last 10 runs
            new_avg = current_avg + (dur - current_avg) / new_count
            
            metrics[pid] = {"avg": new_avg, "count": new_count}
            
    try:
        with open(METRICS_FILE, "w") as f:
            json.dump(metrics, f, indent=2)
    except:
        pass


if __name__ == "__main__":
    main()
