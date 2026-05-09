#!/usr/bin/env python3
import sys
import subprocess
import time
import curses
import select
import queue
import threading
import os

def main(stdscr):
    curses.curs_set(0)
    stdscr.nodelay(True)
    curses.use_default_colors()
    
    q = queue.Queue()
    
    # Run deploy.sh with a special flag so it doesn't loop back to us
    env = os.environ.copy()
    env["ARGUS_NO_UI"] = "1"
    
    # Stdbuf or PYTHONUNBUFFERED is useful but deploy.sh is a bash script.
    # We use pty or just pipe. Pipe is usually fine if we read line by line.
    cmd = ['bash', './deploy/deploy.sh'] + sys.argv[1:]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1, env=env)
    
    def enqueue_output(out, q):
        for line in iter(out.readline, ''):
            if not line: break
            q.put(line)
        out.close()
        
    t = threading.Thread(target=enqueue_output, args=(proc.stdout, q))
    t.daemon = True
    t.start()
    
    # Define tasks based on deploy.sh flow
    tasks = [
        {"name": "Initialize & Fingerprint", "weight": 5, "duration": 5, "progress": 0, "status": "Pending"},
        {"name": "Build Images", "weight": 60, "duration": 80, "progress": 0, "status": "Pending"},
        {"name": "Apply Compose Stack", "weight": 30, "duration": 25, "progress": 0, "status": "Pending"},
        {"name": "Verify & Bootstrap", "weight": 5, "duration": 5, "progress": 0, "status": "Pending"}
    ]
    
    current_task = 0
    start_time = time.time()
    task_start_time = start_time
    logs = []
    
    # Mark first task as running
    tasks[0]["status"] = "Running"
    
    while True:
        try:
            while True:
                line = q.get_nowait()
                line = line.strip()
                if line:
                    if line.startswith('+'):
                        continue
                    logs.append(line)
                    if len(logs) > 12:
                        logs.pop(0)
                    
                    # Heuristics to advance tasks
                    line_lower = line.lower()
                    if "building images" in line_lower or "docker compose build" in line_lower or "rebuilding" in line_lower or "building command-center" in line_lower:
                        if current_task < 1:
                            tasks[0]["progress"] = 100
                            tasks[0]["status"] = "Completed"
                            current_task = 1
                            tasks[1]["status"] = "Running"
                            task_start_time = time.time()
                    elif "applying stack" in line_lower or "docker compose up" in line_lower or "applying core stack" in line_lower:
                        if current_task < 2:
                            tasks[current_task]["progress"] = 100
                            tasks[current_task]["status"] = "Completed"
                            current_task = 2
                            tasks[2]["status"] = "Running"
                            task_start_time = time.time()
                    elif "verifying" in line_lower or "bootstrapping" in line_lower or "argus v2 is running" in line_lower or "creating/updating ecs" in line_lower:
                        if current_task < 3:
                            tasks[current_task]["progress"] = 100
                            tasks[current_task]["status"] = "Completed"
                            current_task = 3
                            tasks[3]["status"] = "Running"
                            task_start_time = time.time()
                    
                    # Docker compose progress lines often have percentages or steps
                    # e.g., "Step 4/10"
                    if current_task == 1 and "step" in line_lower:
                        import re
                        m = re.search(r'step (\d+)/(\d+)', line_lower)
                        if m:
                            step = int(m.group(1))
                            total = int(m.group(2))
                            perc = int((step / total) * 90)
                            if perc > tasks[1]["progress"]:
                                tasks[1]["progress"] = perc

        except queue.Empty:
            pass
            
        # Time-based progress
        if current_task < len(tasks) and tasks[current_task]["status"] == "Running":
            elapsed = time.time() - task_start_time
            expected = tasks[current_task]["duration"]
            perc = min(95, int((elapsed / expected) * 95))
            if perc > tasks[current_task]["progress"]:
                tasks[current_task]["progress"] = perc
                
        # Draw
        stdscr.erase()
        h, w = stdscr.getmaxyx()
        
        stdscr.addstr(1, 2, "🚀 Argus Engine Deployment", curses.A_BOLD)
        
        row = 3
        total_progress = 0
        for i, t in enumerate(tasks):
            total_progress += t["progress"] * (t["weight"] / 100.0)
            status_color = curses.A_NORMAL
            if t["status"] == "Completed":
                status = "[ OK ]"
            elif t["status"] == "Running":
                status = "[ >> ]"
                status_color = curses.A_BOLD
            else:
                status = "[    ]"
            
            bar_len = 40
            filled = int((t["progress"] / 100.0) * bar_len)
            bar = "█" * filled + "░" * (bar_len - filled)
            
            line_str = f"{status} {t['name']:<25} {bar} {t['progress']:>3}%"
            if row < h - 1:
                stdscr.addstr(row, 2, line_str[:w-3], status_color)
            row += 1
            
        row += 1
        if row < h - 1:
            stdscr.addstr(row, 2, f"Overall Progress: {int(total_progress)}%  (Elapsed: {int(time.time() - start_time)}s)", curses.A_BOLD)
        row += 2
        
        if row < h - 1:
            stdscr.addstr(row, 2, "Deployment Logs:", curses.A_DIM)
        row += 1
        for log in logs:
            if row < h - 1:
                # Truncate log to width
                log_trunc = log[:w-3] if len(log) > w-3 else log
                stdscr.addstr(row, 2, log_trunc, curses.A_DIM)
            row += 1
            
        stdscr.refresh()
        
        if proc.poll() is not None and q.empty():
            break
            
        time.sleep(0.1)

    # Finish
    for t in tasks:
        t["progress"] = 100
        t["status"] = "Completed"
    
    stdscr.erase()
    h, w = stdscr.getmaxyx()
    if proc.returncode == 0:
        stdscr.addstr(1, 2, "✅ Deployment Completed Successfully!", curses.A_BOLD)
    else:
        stdscr.addstr(1, 2, f"❌ Deployment Failed with exit code {proc.returncode}.", curses.A_BOLD)
        
    stdscr.addstr(3, 2, f"Total Time: {int(time.time() - start_time)}s")
    
    row = 5
    stdscr.addstr(row, 2, "Final Logs:", curses.A_DIM)
    row += 1
    for log in logs[-15:]:
        if row < h - 2:
            stdscr.addstr(row, 2, log[:w-3])
        row += 1
        
    if row < h - 1:
        stdscr.addstr(row+1, 2, "Press any key to exit...")
    stdscr.refresh()
    
    stdscr.nodelay(False)
    stdscr.getch()

if __name__ == "__main__":
    # Check if curses is available (e.g. not running in a non-tty)
    if not sys.stdout.isatty():
        print("Not running in a TTY. Falling back to plain text deploy.")
        sys.exit(subprocess.call(['bash', './deploy/deploy.sh'] + sys.argv[1:]))
    curses.wrapper(main)
