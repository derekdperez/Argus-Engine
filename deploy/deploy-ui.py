#!/usr/bin/env python3
"""
Argus Engine Deployment UI — v2
Full-screen ANSI terminal dashboard for ./deploy/deploy.sh.
Uses alternate screen buffer so nothing leaks to scrollback.
Tracks structured ARGUS_* markers emitted by the deploy scripts for
accurate per-phase progress, plus counts container Started/Healthy events
for a live Compose-up bar.
"""
import sys, os, subprocess, time, re, threading, queue, signal, shutil

# ── Fall-through if non-interactive ────────────────────────────────────────
if not sys.stdout.isatty():
    deploy_sh = os.path.join(os.path.dirname(os.path.abspath(__file__)), "deploy.sh")
    os.execvp("bash", ["bash", deploy_sh] + sys.argv[1:])

# ── ANSI helpers ────────────────────────────────────────────────────────────
E = "\033"
def mv(r, c):        return f"{E}[{r};{c}H"
def clr():           return f"{E}[2J{E}[H"
def eol():           return f"{E}[K"
def bold():          return f"{E}[1m"
def dim():           return f"{E}[2m"
def rst():           return f"{E}[0m"
def fg(n):           return f"{E}[38;5;{n}m"
def bg(n):           return f"{E}[48;5;{n}m"
def hide_cur():      return f"{E}[?25l"
def show_cur():      return f"{E}[?25h"
def alt_on():        return f"{E}[?1049h"
def alt_off():       return f"{E}[?1049l"

C_CYAN      = fg(51)
C_AMBER     = fg(220)
C_GREEN     = fg(82)
C_RED       = fg(196)
C_GREY      = fg(245)
C_DARK      = fg(238)
C_WHITE     = fg(255)
C_DIM_GREY  = fg(240)
C_BAR_F     = fg(39)
C_BAR_E     = fg(236)
C_SEP       = fg(238)
C_HEADER_BG = bg(234)
C_LOG_ERR   = fg(203)
C_LOG_OK    = fg(77)
C_LOG_NRM   = fg(248)

SPIN = ["⠋","⠙","⠹","⠸","⠼","⠴","⠦","⠧","⠇","⠏"]
BF, BE = "█", "░"

def termsize():
    s = shutil.get_terminal_size((120, 40))
    return s.lines, s.columns

# ── Compose service names (for counting started containers) ─────────────────
# Covers all services defined in docker-compose.yml.
COMPOSE_SERVICES = [
    "postgres", "redis", "rabbitmq", "filestore-db-init",
    "command-center-gateway", "command-center-operations-api",
    "command-center-discovery-api", "command-center-worker-control-api",
    "command-center-maintenance-api", "command-center-updates-api",
    "command-center-realtime", "command-center-bootstrapper",
    "command-center-spider-dispatcher", "command-center-web",
    "gatekeeper",
    "worker-spider", "worker-http-requester", "worker-enum",
    "worker-portscan", "worker-highvalue", "worker-techid",
]
COMPOSE_TOTAL = len(COMPOSE_SERVICES)  # 21 — adjusted at runtime if we see more

# ── Phase definitions ───────────────────────────────────────────────────────
#   id, label, weight(%), estimated wall-seconds (for time-nudge)
PHASE_DEFS = [
    ("init",    "Initialize & Fingerprint",  3,   8),
    ("build",   "Build / Publish",           62, 180),
    ("up",      "Apply Compose Stack",       28,  50),
    ("verify",  "Verify & Finalize",          7,  15),
]

def make_phases():
    return [{
        "id": d[0], "name": d[1], "weight": d[2], "est": d[3],
        "pct": 0, "status": "pending", "detail": "",
    } for d in PHASE_DEFS]

# ── State ───────────────────────────────────────────────────────────────────
class State:
    def __init__(self):
        self.phases = make_phases()
        self.cur    = 0          # active phase index
        self.logs   = []         # all raw log lines
        self.spin_i = 0
        # build sub-tracking
        self.build_svcs_total  = 0
        self.build_svcs_done   = 0
        self.build_svcs_status = {}  # name → "building"|"done"|"fail"
        # parallel publish sub-tracking
        self.pub_svcs_total  = 0
        self.pub_svcs_done   = 0
        self.pub_svcs_status = {}  # name → "publishing"|"ok"|"fail"
        # compose-up sub-tracking (accurate)
        self.compose_started = set()   # container names that emitted Started/Healthy
        self.compose_total   = COMPOSE_TOTAL
        # timing
        self.start      = time.time()
        self.phase_start= time.time()

    def phase(self): return self.phases[self.cur] if self.cur < len(self.phases) else None
    def spin(self):
        self.spin_i = (self.spin_i + 1) % len(SPIN)
        return SPIN[self.spin_i]

    def advance(self, phase_id: str):
        """Mark current phase done and activate phase_id."""
        for i, p in enumerate(self.phases):
            if p["status"] == "running":
                p["pct"] = 100
                p["status"] = "done"
            if p["id"] == phase_id and p["status"] == "pending":
                p["status"] = "running"
                self.cur = i
                self.phase_start = time.time()
                break

    def overall_pct(self):
        total_w = sum(p["weight"] for p in self.phases)
        done_w  = sum(p["pct"] * p["weight"] / 100 for p in self.phases)
        return min(100, int(done_w * 100 / total_w))

# ── Log parser ──────────────────────────────────────────────────────────────
def feed(state: State, raw: str):
    line = raw.rstrip()
    if not line: return
    ll = line.lower()

    # Skip bash trace lines and BuildKit internal lines
    if line.startswith("+ ") or line.startswith("++ "): return
    if re.match(r'^#\d+ \[', line): return      # BuildKit step lines

    state.logs.append(line)
    if len(state.logs) > 500: state.logs = state.logs[-500:]

    # ── Structured markers from deploy scripts ──────────────────────────────
    m = re.match(r'^ARGUS_PHASE:(\S+)', line)
    if m:
        state.advance(m.group(1))
        return

    m = re.match(r'^ARGUS_FAST_HOT_SWAP_START:(.+)', line)
    if m:
        svcs = m.group(1).split()
        state.pub_svcs_total = len(svcs)
        for s in svcs:
            state.pub_svcs_status[s] = "publishing"
        state.advance("build")
        return

    m = re.match(r'^ARGUS_PUBLISH_START:(\S+)', line)
    if m:
        state.pub_svcs_status[m.group(1)] = "publishing"
        state.pub_svcs_total = max(state.pub_svcs_total, len(state.pub_svcs_status))
        return

    m = re.match(r'^ARGUS_STATUS:(\S+):(ok|fail)', line)
    if m:
        svc, result = m.group(1), m.group(2)
        state.pub_svcs_status[svc] = result
        state.pub_svcs_done = sum(1 for v in state.pub_svcs_status.values() if v in ("ok","fail"))
        _update_build_pct(state)
        return

    m = re.match(r'^ARGUS_COPY_DONE:(\S+)', line)
    if m:
        ph = _find_phase(state, "build")
        if ph: ph["detail"] = f"Copying → {m.group(1)}"
        return

    m = re.match(r'^ARGUS_ALL_COPIES_DONE', line)
    if m:
        ph = _find_phase(state, "build")
        if ph: ph["detail"] = "Restarting services…"
        return

    m = re.match(r'^ARGUS_RESTART_DONE:(\S+)', line)
    if m:
        ph = _find_phase(state, "build")
        if ph: ph["detail"] = f"Restarted {m.group(1)}"
        return

    m = re.match(r'^ARGUS_ALL_RESTARTS_DONE', line)
    if m:
        state.advance("up")
        return

    # ── Init phase signals ─────────────────────────────────────────────────
    if state.cur == 0:
        ph = state.phases[0]
        ph["status"] = "running"
        if any(x in ll for x in ["build_source_stamp", "hot deploy plan", "fresh deploy",
                                   "fast deploy", "skip build", "image rebuild service"]):
            # Parse how many services will be built
            m2 = re.search(r'image rebuild service.*?:\s*(.+)', line, re.I)
            if m2:
                svcs = m2.group(1).split()
                state.build_svcs_total = len(svcs)
                for s in svcs:
                    state.build_svcs_status[s] = "pending"

            if "skip build" in ll or "no unapplied" in ll:
                ph["pct"] = 100; ph["status"] = "done"
                _find_phase(state, "build")["pct"] = 100
                _find_phase(state, "build")["status"] = "skipped"
                state.advance("up")
            else:
                ph["pct"] = 100; ph["status"] = "done"
                state.advance("build")
        else:
            if ph["pct"] < 85:
                ph["pct"] = min(85, ph["pct"] + 10)
        ph["detail"] = line[:90]
        return

    # ── Build phase: Docker BuildKit plain-text parsing ────────────────────
    if _find_phase(state, "build") and _find_phase(state, "build")["status"] == "running":
        ph = _find_phase(state, "build")

        # Service completed (exporting or named-image writing)
        m2 = re.match(r'^#\d+\s+\[([a-z0-9_-]+)\s+(?:build|final)\s+\d+/\d+\]\s+.*DONE', line, re.I)
        if not m2:
            m2 = re.match(r'^\s*✔\s+([a-z0-9_-]+).*(?:Built|Done)', line, re.I)
        if m2:
            svc = m2.group(1).replace("_","-")
            if state.build_svcs_status.get(svc) != "done":
                state.build_svcs_status[svc] = "done"
                state.build_svcs_done += 1
                _update_build_pct(state)

        # Build step step X/Y per service
        m3 = re.match(r'^#\d+\s+\[([a-z0-9_-]+)\s+build\s+(\d+)/(\d+)\]', line, re.I)
        if m3:
            svc = m3.group(1).replace("_","-")
            step, total = int(m3.group(2)), int(m3.group(3))
            state.build_svcs_status[svc] = f"{step}/{total}"
            if state.build_svcs_total == 0: state.build_svcs_total = 1
            ph["detail"] = f"Building {svc} ({step}/{total})"
            _update_build_pct(state)

        # Publish lines from fast-hot-swap
        m4 = re.search(r'Publishing\s+(\S+)\s+with cached', line, re.I)
        if m4:
            ph["detail"] = f"Publishing {m4.group(1)}"

        # Transition to up: compose starting containers
        if any(x in ll for x in ["running network argus", "network argus", "creating argus-engine",
                                   "starting argus-engine", "container argus"]):
            ph["pct"] = 100; ph["status"] = "done"
            state.advance("up")
        return

    # ── Compose-up phase: count Started / Healthy events ──────────────────
    if _find_phase(state, "up") and _find_phase(state, "up")["status"] == "running":
        ph = _find_phase(state, "up")

        # docker compose up lines: " ✔ Container argus-engine-postgres-1  Healthy"
        # or:                      " Container argus-engine-web-1  Starting"
        m2 = re.search(r'container\s+(argus-engine-[a-z0-9_-]+-\d+)\s+(started|healthy|running|created)', ll)
        if m2:
            cname = m2.group(1)
            state.compose_started.add(cname)
            # dynamically grow total if we see more containers than expected
            state.compose_total = max(state.compose_total, len(state.compose_started) + 1)
            pct = int(len(state.compose_started) / state.compose_total * 95)
            ph["pct"] = max(ph["pct"], pct)
            ph["detail"] = f"{len(state.compose_started)}/{state.compose_total} containers up"

        # Detect end of compose up
        if any(x in ll for x in ["argus v2 is running", "useful commands", "command center gateway"]):
            # Mark every remaining container as done
            ph["pct"] = 100; ph["status"] = "done"
            state.advance("verify")
        return

    # ── Verify phase ───────────────────────────────────────────────────────
    if _find_phase(state, "verify") and _find_phase(state, "verify")["status"] == "running":
        ph = _find_phase(state, "verify")
        ph["detail"] = line[:90]
        ph["pct"] = min(95, ph["pct"] + 15)
        return


def _find_phase(state, pid):
    for p in state.phases:
        if p["id"] == pid: return p
    return None

def _update_build_pct(state):
    ph = _find_phase(state, "build")
    if not ph or ph["status"] not in ("running",): return
    total = max(state.build_svcs_total, state.pub_svcs_total, 1)
    done  = max(state.build_svcs_done,  state.pub_svcs_done)
    frac  = done / total
    ph["pct"] = max(ph["pct"], min(95, int(frac * 95)))
    if done >= total > 0:
        ph["detail"] = f"All {total} services built"

# ── Time-nudge (moves bar forward using expected duration when no events) ───
def time_nudge(state):
    ph = state.phase()
    if not ph or ph["status"] != "running": return
    elapsed  = time.time() - state.phase_start
    est      = ph["est"]
    # Logarithmic: fast early, slow as it approaches 95%
    target   = min(95, int(95 * (1 - 2 ** (-elapsed / max(est, 1) * 1.5))))
    if target > ph["pct"]:
        ph["pct"] = target

# ── Renderer ────────────────────────────────────────────────────────────────
def render(state: State, final=False, rc=None):
    rows, cols = termsize()
    out = []
    W = cols

    def line(row, text):
        out.append(f"{mv(row,1)}{text}{eol()}")

    def hbar(row):
        out.append(f"{mv(row,1)}{C_SEP}{'─'*W}{rst()}")

    # ── Header ──────────────────────────────────────────────────────────────
    title = "  ARGUS ENGINE  ▸  DEPLOYMENT  "
    pad   = max(0, (W - len(title)) // 2)
    line(1, f"{C_HEADER_BG}{C_CYAN}{bold()}{' '*pad}{title}{' '*(W-pad-len(title))}{rst()}")

    elapsed = time.time() - state.start
    mins, secs = divmod(int(elapsed), 60)
    mode = " ".join(sys.argv[1:]) or "hot-deploy"
    line(2, f"  {dim()}{C_GREY}Elapsed: {mins:02d}:{secs:02d}   Mode: {mode}{rst()}")
    hbar(3)

    # ── Phase bars ──────────────────────────────────────────────────────────
    row = 4
    BAR_W = max(20, W - 50)
    spin_char = state.spin() if not final else " "

    for ph in state.phases:
        pct    = ph["pct"]
        status = ph["status"]

        if status == "done":
            icon = f"{C_GREEN}✔{rst()}"; nc = C_GREEN
        elif status == "skipped":
            icon = f"{C_GREY}–{rst()}"; nc = C_GREY
        elif status == "running":
            icon = f"{C_AMBER}{spin_char}{rst()}"; nc = f"{C_AMBER}{bold()}"
        elif status == "failed":
            icon = f"{C_RED}✘{rst()}"; nc = C_RED
        else:
            icon = f"{C_DARK}·{rst()}"; nc = C_DARK

        filled = int(pct / 100 * BAR_W)
        bar    = f"{C_BAR_F}{BF*filled}{C_BAR_E}{BE*(BAR_W-filled)}{rst()}"
        pct_s  = f"{bold()}{pct:3d}%{rst()}"
        name_s = f"{nc}{ph['name']:<28}{rst()}"
        line(row, f"  {icon}  {name_s}  {bar}  {pct_s}")
        row += 1

        detail = ph.get("detail","")
        if status == "running" and ph["id"] == "build":
            pub_t = state.pub_svcs_total; pub_d = state.pub_svcs_done
            bld_t = state.build_svcs_total; bld_d = state.build_svcs_done
            if pub_t > 0:
                detail = f"Publishing in parallel: {pub_d}/{pub_t}   {detail}"
            elif bld_t > 0:
                detail = f"Images: {bld_d}/{bld_t}   {detail}"
        if status == "running" and ph["id"] == "up" and state.compose_started:
            detail = f"Containers up: {len(state.compose_started)}/{state.compose_total}"

        if detail:
            line(row, f"     {dim()}{C_GREY}{detail[:W-8]}{rst()}")
            row += 1

    hbar(row); row += 1

    # ── Overall bar ─────────────────────────────────────────────────────────
    ov = state.overall_pct()
    OW = W - 18
    of = int(ov / 100 * OW)
    ov_bar = f"{C_GREEN}{BF*of}{C_BAR_E}{BE*(OW-of)}{rst()}"
    line(row, f"  {C_WHITE}{bold()}Overall  {rst()}{ov_bar}  {bold()}{ov:3d}%{rst()}")
    row += 1
    hbar(row); row += 1

    # ── Final status ─────────────────────────────────────────────────────────
    if final:
        if rc == 0:
            line(row, f"  {C_GREEN}{bold()}✔  Deployment complete  — {mins:02d}:{secs:02d}{rst()}")
        else:
            line(row, f"  {C_RED}{bold()}✘  Deployment FAILED (exit {rc})  — {mins:02d}:{secs:02d}{rst()}")
        row += 1
        hbar(row); row += 1

    # ── Log pane ─────────────────────────────────────────────────────────────
    log_rows = rows - row - 1
    line(row, f"  {dim()}{C_GREY}Output{rst()}"); row += 1

    visible = [l for l in state.logs
               if not (l.startswith("+ ") or l.startswith("++ ")
                       or re.match(r'^#\d+ \[', l)
                       or re.match(r'^ARGUS_', l))][-max(3, log_rows):]

    for lg in visible:
        if row >= rows: break
        if any(x in lg.lower() for x in ("error","fatal","fail","cannot")):
            c = C_LOG_ERR
        elif any(x in lg.lower() for x in ("done","started","healthy","ok","complete")):
            c = C_LOG_OK
        else:
            c = C_LOG_NRM
        line(row, f"  {c}{lg[:W-4]}{rst()}")
        row += 1

    while row <= rows - 1:
        out.append(f"{mv(row,1)}{eol()}"); row += 1

    if final:
        out.append(f"{mv(rows,1)}{dim()}  Press Enter to exit…{rst()}{eol()}")

    sys.stdout.write("".join(out))
    sys.stdout.flush()


# ── Main ────────────────────────────────────────────────────────────────────
def main():
    deploy_dir = os.path.dirname(os.path.abspath(__file__))
    env = os.environ.copy()
    env["ARGUS_NO_UI"]        = "1"
    env["BUILDKIT_PROGRESS"]  = "plain"

    cmd  = ["bash", os.path.join(deploy_dir, "deploy.sh")] + sys.argv[1:]
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

    sys.stdout.write(alt_on() + hide_cur() + clr())
    sys.stdout.flush()

    def cleanup(sig=None, _frame=None):
        sys.stdout.write(show_cur() + alt_off())
        sys.stdout.flush()
        if proc.poll() is None: proc.terminate()
        sys.exit(130)

    signal.signal(signal.SIGINT, cleanup)
    signal.signal(signal.SIGTERM, cleanup)

    tick = 0
    done = False
    try:
        while not done:
            drained = 0
            while drained < 80:
                try:
                    item = logQ.get_nowait()
                except queue.Empty:
                    break
                if item is None:
                    done = True; break
                feed(state, item)
                drained += 1

            time_nudge(state)

            if tick % 2 == 0:
                render(state)
            tick += 1
            if not done:
                time.sleep(0.1)

        proc.wait()
        elapsed = time.time() - state.start

        # Finalise phases on success
        if proc.returncode == 0:
            for p in state.phases:
                if p["status"] not in ("skipped",):
                    p["status"] = "done"; p["pct"] = 100
        else:
            for p in state.phases:
                if p["status"] == "running":
                    p["status"] = "failed"

        render(state, final=True, rc=proc.returncode)
        try: sys.stdin.readline()
        except Exception: time.sleep(5)

    finally:
        sys.stdout.write(show_cur() + alt_off())
        sys.stdout.flush()

    sys.exit(proc.returncode)


if __name__ == "__main__":
    main()
