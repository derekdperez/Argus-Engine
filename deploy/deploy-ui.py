#!/usr/bin/env python3
"""
Argus Engine Deployment UI
Full-screen terminal progress display for ./deploy/deploy.sh
"""
import sys
import os
import subprocess
import time
import re
import threading
import queue
import signal

# ---------------------------------------------------------------------------
# Detect terminal capability – fall back to plain if non-interactive
# ---------------------------------------------------------------------------
if not sys.stdout.isatty():
    os.execvp("bash", ["bash", os.path.join(os.path.dirname(__file__), "deploy.sh")] + sys.argv[1:])
    sys.exit(1)  # unreachable

# ---------------------------------------------------------------------------
# ANSI helpers (no curses dependency – far more reliable over SSH)
# ---------------------------------------------------------------------------
ESC = "\033"

def ansi(*codes): return f"{ESC}[{';'.join(str(c) for c in codes)}m"
def move(row, col): return f"{ESC}[{row};{col}H"
def clear_screen(): return f"{ESC}[2J{ESC}[H"
def clear_eol(): return f"{ESC}[K"
def hide_cursor(): return f"{ESC}[?25l"
def show_cursor(): return f"{ESC}[?25h"
def alt_screen_on(): return f"{ESC}[?1049h"
def alt_screen_off(): return f"{ESC}[?1049l"

def get_term_size():
    try:
        import shutil
        s = shutil.get_terminal_size((120, 40))
        return s.lines, s.columns
    except Exception:
        return 40, 120

# Color palette (256-color)
C_RESET      = ansi(0)
C_BOLD       = ansi(1)
C_DIM        = ansi(2)

C_HEADER_BG  = f"{ESC}[48;5;234m"   # very dark grey bg
C_HEADER_FG  = f"{ESC}[38;5;51m"    # cyan
C_LABEL      = f"{ESC}[38;5;245m"   # medium grey
C_GOOD       = f"{ESC}[38;5;82m"    # bright green
C_RUNNING    = f"{ESC}[38;5;220m"   # amber/yellow
C_PENDING    = f"{ESC}[38;5;238m"   # dark grey
C_FAIL       = f"{ESC}[38;5;196m"   # red
C_BAR_FILL   = f"{ESC}[38;5;39m"    # blue-cyan
C_BAR_EMPTY  = f"{ESC}[38;5;236m"   # very dark grey
C_LOG_FG     = f"{ESC}[38;5;250m"   # light grey
C_LOG_MUTED  = f"{ESC}[38;5;243m"   # dark grey
C_OVERALL_FG = f"{ESC}[38;5;255m"   # near-white
C_SEP        = f"{ESC}[38;5;240m"   # separator line

BAR_FULL  = "█"
BAR_HALF  = "▓"
BAR_EMPTY = "░"

SPINNER_FRAMES = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]

# ---------------------------------------------------------------------------
# Phase definitions  (name, weight-%, expected-seconds)
# ---------------------------------------------------------------------------
PHASES = [
    {"id": "init",    "name": "Initialize & Fingerprint", "weight": 3,  "est": 8,   "progress": 0, "status": "pending", "detail": ""},
    {"id": "build",   "name": "Build Docker Images",      "weight": 65, "est": 180, "progress": 0, "status": "pending", "detail": "", "services": {}, "done_services": 0, "total_services": 0},
    {"id": "up",      "name": "Apply Compose Stack",      "weight": 25, "est": 45,  "progress": 0, "status": "pending", "detail": ""},
    {"id": "verify",  "name": "Verify & Finalize",        "weight": 7,  "est": 20,  "progress": 0, "status": "pending", "detail": ""},
]

# ---------------------------------------------------------------------------
# Log parser – maps raw lines to phase transitions & progress
# ---------------------------------------------------------------------------
class DeployParser:
    def __init__(self):
        self.current = 0          # index into PHASES
        self.logs = []            # recent log lines (raw)
        self._spinner = 0
        # Per-service build tracking for the build phase
        # key = service name, value = {"steps": N, "total": N, "done": bool}
        self._build_services = {}
        self._build_services_done = 0
        self._total_build_svcs = 0

    # -- Returns list of phases updated (so UI knows to redraw) --
    def feed(self, raw: str):
        line = raw.rstrip()
        if not line:
            return
        # Always keep logs but skip pure bash trace lines
        if not (line.startswith("+ ") or line.startswith("++ ")):
            self.logs.append(line)
            if len(self.logs) > 200:
                self.logs = self.logs[-200:]

        ll = line.lower()

        # ---- Phase 0: Init -----------------------------------------------
        if self.current == 0:
            PHASES[0]["status"] = "running"
            if any(x in ll for x in ["build_source_stamp", "hot deploy plan", "fresh deploy", "fast deploy",
                                      "image rebuild service", "skip build", "applying stack"]):
                # Completed init; detect how many services will be built
                m = re.search(r'image rebuild service.*?:\s*(.+)', line, re.IGNORECASE)
                if m:
                    svcs = m.group(1).split()
                    self._total_build_svcs = len(svcs)
                    for s in svcs:
                        self._build_services[s] = {"steps": 0, "total": 10, "done": False}
                    PHASES[1]["total_services"] = self._total_build_svcs

                if "skip build" in ll or "no unapplied" in ll:
                    # No build needed – skip straight to compose up
                    PHASES[0]["progress"] = 100
                    PHASES[0]["status"] = "done"
                    PHASES[1]["progress"] = 100
                    PHASES[1]["status"] = "skipped"
                    self.current = 2
                    PHASES[2]["status"] = "running"
                else:
                    PHASES[0]["progress"] = 100
                    PHASES[0]["status"] = "done"
                    self.current = 1
                    PHASES[1]["status"] = "running"
                return

            # Bump init progress on any meaningful line
            if PHASES[0]["progress"] < 90:
                PHASES[0]["progress"] = min(90, PHASES[0]["progress"] + 5)
            PHASES[0]["detail"] = line[:80]

        # ---- Phase 1: Build -----------------------------------------------
        elif self.current == 1:
            PHASES[1]["detail"] = ""

            # Detect service build completion  (#N [service-name ...] DONE)
            m_done = re.match(r'#\d+\s+\[([a-z0-9_-]+)\s+[^\]]*\]\s+(?:exporting|CACHED|DONE)', line, re.IGNORECASE)
            if not m_done:
                m_done = re.match(r'#\d+\s+\[([a-z0-9_-]+)\s+final', line, re.IGNORECASE)

            # Track per-service step progress from: #N [service build X/Y]
            m_step = re.match(r'#\d+\s+\[([a-z0-9_-]+)\s+build\s+(\d+)/(\d+)\]', line, re.IGNORECASE)
            if m_step:
                svc = m_step.group(1).replace("_", "-")
                step = int(m_step.group(2))
                total = int(m_step.group(3))
                if svc not in self._build_services:
                    self._build_services[svc] = {"steps": 0, "total": total, "done": False}
                    if self._total_build_svcs == 0:
                        self._total_build_svcs += 1
                entry = self._build_services[svc]
                entry["steps"] = max(entry["steps"], step)
                entry["total"] = max(entry["total"], total)
                PHASES[1]["detail"] = f"Building {svc} ({step}/{total})"

            # Mark service done on exporting / done
            m_export = re.match(r'#\d+\s+\[([a-z0-9_-]+)\s+(?:final|export)', line, re.IGNORECASE)
            if m_export:
                svc = m_export.group(1).replace("_", "-")
                if svc in self._build_services and not self._build_services[svc]["done"]:
                    self._build_services[svc]["done"] = True
                    self._build_services[svc]["steps"] = self._build_services[svc]["total"]
                    self._build_services_done += 1

            # Compute aggregate build progress
            if self._total_build_svcs > 0 and self._build_services:
                total_steps = sum(max(e["total"], 1) for e in self._build_services.values())
                done_steps  = sum(min(e["steps"], e["total"]) for e in self._build_services.values())
                frac = done_steps / max(total_steps, 1)
                PHASES[1]["progress"] = max(PHASES[1]["progress"], min(95, int(frac * 95)))
                PHASES[1]["done_services"] = self._build_services_done
                PHASES[1]["total_services"] = self._total_build_svcs

            # Detect build end → compose up
            if any(x in ll for x in ["running network", "container started", "starting argus",
                                      "creating argus", "started", "network argus"]):
                PHASES[1]["progress"] = 100
                PHASES[1]["status"] = "done"
                self.current = 2
                PHASES[2]["status"] = "running"

        # ---- Phase 2: Compose up ------------------------------------------
        elif self.current == 2:
            if any(x in ll for x in ["argus v2 is running", "command center gateway", "useful commands"]):
                PHASES[2]["progress"] = 100
                PHASES[2]["status"] = "done"
                self.current = 3
                PHASES[3]["status"] = "running"
                PHASES[3]["detail"] = "Finalizing..."
            elif any(x in ll for x in ["started", "healthy", "created", "running", "waiting"]):
                PHASES[2]["detail"] = line[:80]
                PHASES[2]["progress"] = min(95, PHASES[2]["progress"] + 3)

        # ---- Phase 3: Verify ----------------------------------------------
        elif self.current == 3:
            PHASES[3]["detail"] = line[:80]
            PHASES[3]["progress"] = min(95, PHASES[3]["progress"] + 10)

    def spinner(self):
        self._spinner = (self._spinner + 1) % len(SPINNER_FRAMES)
        return SPINNER_FRAMES[self._spinner]

    def overall_pct(self):
        total = sum(p["weight"] for p in PHASES)
        done  = sum(p["progress"] * p["weight"] / 100 for p in PHASES)
        return min(100, int(done * 100 / total))


# ---------------------------------------------------------------------------
# Renderer
# ---------------------------------------------------------------------------
def render(parser: DeployParser, elapsed: float, phase_elapsed: float):
    rows, cols = get_term_size()
    buf = []
    w = cols

    def wr(row, col, text):
        buf.append(f"{move(row, col)}{text}{clear_eol()}")

    def hline(row, ch="─"):
        buf.append(f"{move(row, 1)}{C_SEP}{ch * (w)}{C_RESET}")

    row = 1

    # ── Header ───────────────────────────────────────────────────────────────
    header_text = " ARGUS ENGINE  ▸  DEPLOYMENT "
    pad = (w - len(header_text)) // 2
    wr(row, 1, f"{C_HEADER_BG}{C_HEADER_FG}{C_BOLD}{' ' * pad}{header_text}{' ' * (w - pad - len(header_text))}{C_RESET}")
    row += 1

    # elapsed time + mode
    mode = " ".join(sys.argv[1:]) if len(sys.argv) > 1 else "hot-deploy"
    time_str = f"  Elapsed: {int(elapsed // 60):02d}:{int(elapsed % 60):02d}  │  Mode: {mode}"
    wr(row, 1, f"{C_DIM}{time_str}{C_RESET}")
    row += 1
    hline(row); row += 1

    # ── Phase rows ───────────────────────────────────────────────────────────
    bar_width = max(20, w - 46)
    for ph in PHASES:
        pct    = ph["progress"]
        status = ph["status"]

        if status == "done" or status == "skipped":
            icon = f"{C_GOOD}✔{C_RESET}"
            name_color = C_GOOD
        elif status == "running":
            icon = f"{C_RUNNING}{parser.spinner()}{C_RESET}"
            name_color = C_RUNNING + C_BOLD
        elif status == "failed":
            icon = f"{C_FAIL}✘{C_RESET}"
            name_color = C_FAIL
        else:
            icon = f"{C_PENDING}·{C_RESET}"
            name_color = C_PENDING

        filled = int(pct / 100 * bar_width)
        bar = (f"{C_BAR_FILL}{BAR_FULL * filled}{C_BAR_EMPTY}{BAR_EMPTY * (bar_width - filled)}{C_RESET}")

        pct_str = f"{C_BOLD}{pct:3d}%{C_RESET}"
        name_str = f"{name_color}{ph['name']:<28}{C_RESET}"

        wr(row, 1, f"  {icon}  {name_str}  {bar}  {pct_str}")
        row += 1

        # Detail / sub-info line
        detail = ph.get("detail", "")
        if ph["id"] == "build" and ph["status"] == "running":
            done_s = ph.get("done_services", 0)
            total_s = ph.get("total_services", 0)
            if total_s:
                detail = f"Services built: {done_s}/{total_s}   {ph.get('detail', '')}"
        if detail:
            detail_trunc = detail[:w - 8]
            wr(row, 1, f"     {C_LOG_MUTED}{detail_trunc}{C_RESET}")
            row += 1

    hline(row); row += 1

    # ── Overall progress ─────────────────────────────────────────────────────
    overall = parser.overall_pct()
    ow = w - 18
    of = int(overall / 100 * ow)
    overall_bar = f"{C_GOOD}{BAR_FULL * of}{C_BAR_EMPTY}{BAR_EMPTY * (ow - of)}{C_RESET}"
    wr(row, 1, f"  {C_OVERALL_FG}{C_BOLD}Overall  {C_RESET}{overall_bar}  {C_BOLD}{overall:3d}%{C_RESET}")
    row += 1
    hline(row); row += 1

    # ── Log pane ─────────────────────────────────────────────────────────────
    log_rows = rows - row - 1
    if log_rows < 3:
        log_rows = 3

    wr(row, 1, f"  {C_LABEL}Recent Output{C_RESET}")
    row += 1

    # Filter interesting log lines
    visible_logs = [l for l in parser.logs
                    if not (l.startswith("+ ") or l.startswith("++ ") or l.startswith("#"))]
    # Take last N that fit
    visible_logs = visible_logs[-log_rows:]
    for log in visible_logs:
        trunc = log[:w - 4]
        color = C_FAIL if any(x in log.lower() for x in ["error", "fatal", "fail"]) else \
                C_GOOD if any(x in log.lower() for x in ["done", "success", "ok", "running"]) else C_LOG_MUTED
        wr(row, 1, f"  {color}{trunc}{C_RESET}")
        row += 1

    # Clear remaining lines in log area
    while row <= rows - 1:
        buf.append(f"{move(row, 1)}{clear_eol()}")
        row += 1

    sys.stdout.write("".join(buf))
    sys.stdout.flush()


def render_final(parser: DeployParser, elapsed: float, returncode: int):
    rows, cols = get_term_size()
    buf = []

    def wr(row, col, text):
        buf.append(f"{move(row, col)}{text}{clear_eol()}")

    def hline(row):
        buf.append(f"{move(row, 1)}{C_SEP}{'─' * cols}{C_RESET}")

    # Final state – mark all done or last phase failed
    if returncode != 0:
        for ph in PHASES:
            if ph["status"] == "running":
                ph["status"] = "failed"
                ph["progress"] = ph["progress"]
    else:
        for ph in PHASES:
            if ph["status"] != "skipped":
                ph["status"] = "done"
                ph["progress"] = 100

    row = 1
    # Header
    header_text = " ARGUS ENGINE  ▸  DEPLOYMENT "
    pad = (cols - len(header_text)) // 2
    wr(row, 1, f"{C_HEADER_BG}{C_HEADER_FG}{C_BOLD}{' ' * pad}{header_text}{' ' * (cols - pad - len(header_text))}{C_RESET}")
    row += 1

    mins, secs = divmod(int(elapsed), 60)
    wr(row, 1, f"  {C_DIM}Elapsed: {mins:02d}:{secs:02d}{C_RESET}")
    row += 1
    hline(row); row += 1

    bar_width = max(20, cols - 46)
    for ph in PHASES:
        pct    = ph["progress"]
        status = ph["status"]
        if status in ("done", "skipped"):
            icon = f"{C_GOOD}✔{C_RESET}"; name_color = C_GOOD
        elif status == "failed":
            icon = f"{C_FAIL}✘{C_RESET}"; name_color = C_FAIL
        else:
            icon = f"{C_PENDING}·{C_RESET}"; name_color = C_PENDING

        filled = int(pct / 100 * bar_width)
        bar = f"{C_BAR_FILL}{BAR_FULL * filled}{C_BAR_EMPTY}{BAR_EMPTY * (bar_width - filled)}{C_RESET}"
        wr(row, 1, f"  {icon}  {name_color}{ph['name']:<28}{C_RESET}  {bar}  {C_BOLD}{pct:3d}%{C_RESET}")
        row += 1

    hline(row); row += 1

    if returncode == 0:
        msg = f"  {C_GOOD}{C_BOLD}✔  Deployment complete  —  {mins:02d}:{secs:02d}{C_RESET}"
    else:
        msg = f"  {C_FAIL}{C_BOLD}✘  Deployment FAILED  (exit {returncode})  —  {mins:02d}:{secs:02d}{C_RESET}"
    wr(row, 1, msg); row += 1
    hline(row); row += 1

    # Last logs
    wr(row, 1, f"  {C_LABEL}Last output:{C_RESET}"); row += 1
    log_rows = rows - row - 2
    visible = [l for l in parser.logs if not (l.startswith("+ ") or l.startswith("++ "))]
    for log in visible[-max(5, log_rows):]:
        color = C_FAIL if any(x in log.lower() for x in ["error", "fatal", "fail"]) else C_LOG_MUTED
        wr(row, 1, f"  {color}{log[:cols-4]}{C_RESET}")
        row += 1
        if row >= rows - 1:
            break

    wr(rows - 1, 1, f"  {C_DIM}Press Enter to exit…{C_RESET}")
    sys.stdout.write("".join(buf))
    sys.stdout.flush()


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
def main():
    env = os.environ.copy()
    env["ARGUS_NO_UI"] = "1"
    env["BUILDKIT_PROGRESS"] = "plain"
    env["TERM"] = env.get("TERM", "xterm-256color")

    deploy_dir = os.path.dirname(os.path.abspath(__file__))
    cmd = ["bash", os.path.join(deploy_dir, "deploy.sh")] + sys.argv[1:]

    log_q: queue.Queue[str | None] = queue.Queue()

    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
        env=env,
        cwd=os.path.dirname(deploy_dir),
    )

    def reader():
        for line in iter(proc.stdout.readline, ""):
            log_q.put(line)
        log_q.put(None)  # sentinel

    threading.Thread(target=reader, daemon=True).start()

    parser = DeployParser()
    start  = time.time()
    phase_start = start

    # Enter alternate screen, hide cursor
    sys.stdout.write(alt_screen_on() + hide_cursor() + clear_screen())
    sys.stdout.flush()

    def cleanup(sig=None, frame=None):
        sys.stdout.write(show_cursor() + alt_screen_off())
        sys.stdout.flush()
        if proc.poll() is None:
            proc.terminate()
        sys.exit(130)

    signal.signal(signal.SIGINT, cleanup)
    signal.signal(signal.SIGTERM, cleanup)

    done = False
    tick = 0
    try:
        while not done:
            # Drain queue
            drained = 0
            while drained < 50:
                try:
                    item = log_q.get_nowait()
                except queue.Empty:
                    break
                if item is None:
                    done = True
                    break
                parser.feed(item)
                drained += 1

            # Time-based progress nudge for the active phase
            now = time.time()
            elapsed = now - start
            ph = PHASES[parser.current] if parser.current < len(PHASES) else None
            if ph and ph["status"] in ("running",):
                ph_elapsed = now - phase_start
                est = ph.get("est", 60)
                # Smooth log-curve: fast at first, slows as it approaches 95%
                nudge = min(95, int(95 * (1 - 2 ** (-ph_elapsed / est * 1.5))))
                if nudge > ph["progress"]:
                    ph["progress"] = nudge
                    phase_start_prev = phase_start
            
            # Detect phase advancement (status changed to done) → reset phase_start
            for i, p in enumerate(PHASES):
                if p["status"] == "done" and i == parser.current - 1:
                    phase_start = now

            # Redraw every 100ms (every other tick)
            if tick % 2 == 0:
                render(parser, elapsed, now - phase_start)
            tick += 1

            if not done:
                time.sleep(0.1)

        # Wait for process
        proc.wait()
        elapsed = time.time() - start
        render_final(parser, elapsed, proc.returncode)

        # Wait for Enter
        try:
            sys.stdin.readline()
        except Exception:
            time.sleep(3)

    finally:
        sys.stdout.write(show_cursor() + alt_screen_off())
        sys.stdout.flush()

    sys.exit(proc.returncode)


if __name__ == "__main__":
    main()
