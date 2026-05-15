# AI Agent Coordination Protocol

## Overview

This system allows multiple AI agents to work on the same codebase without overwriting each other's changes. Coordination is enforced through a shared status file and a strict git workflow.

## Core Rules

### 1. Always Pull Before Making Any Changes
Before reading or modifying any file, run:
```bash
git pull --rebase
```

### 2. Claim Files Before Working On Them
Run the coordination script to claim a file:
```bash
bash .ai-coordination/scripts/coordinate.sh claim <agent-name> <file-path> "<purpose>"
```

Example:
```bash
bash .ai-coordination/scripts/coordinate.sh claim agent-alpha src/ArgusEngine.Application/SomeFile.cs "Implement feature X"
```

This will:
- Pull latest changes from git
- Check if the file is already locked by another agent
- If locked: display who holds the lock and their purpose, then exit
- If free: write the lock to status.json, commit, and push

### 3. Release Files When Done
After finishing work on a file, release it:
```bash
bash .ai-coordination/scripts/coordinate.sh release <agent-name> <file-path>
```

### 4. Push Immediately After Every Change
After ANY modification to any file:
```bash
bash .ai-coordination/scripts/coordinate.sh push <agent-name> "<commit message>"
```

This will:
- Stage all changes
- Pull latest from remote (to catch any conflicts early)
- Commit and push

### 5. Update Heartbeat Periodically
To show you are still actively working:
```bash
bash .ai-coordination/scripts/coordinate.sh heartbeat <agent-name>
```

### 6. Declare Shared Files (for files multiple agents need to edit)
If you need to work on a file that another agent is already working on (or will likely work on):
```bash
bash .ai-coordination/scripts/coordinate.sh share <agent-name> <file-path>
```

This marks the file as "shared". When the second agent pushes, they must merge both sets of changes. The script will help with merge conflict resolution.

### 7. When Merge Conflicts Occur
The script will detect merge conflicts automatically during push. When conflicts occur:

1. The script will abort the merge and display the conflicting files
2. Use `git diff` to see both sides:
   ```bash
   git diff
   ```
3. The conflicting markers in files show:
   - `<<<<<<< HEAD` = your changes
   - `=======` = divider
   - `>>>>>>> remote` = the other agent's changes
4. Edit the file to KEEP BOTH sets of changes (do not discard either agent's work unless they conflict directly on the same lines)
5. After resolving manually, run:
   ```bash
   git add <resolved-files>
   git commit -m "merge: resolve conflicts between agents"
   git push
   ```

**Critical: Never discard another agent's changes. If both agents modified the same function, find a way to include both contributions.**

## Status File Structure

The file `.ai-coordination/status.json` is the single source of truth. Its structure:

```json
{
  "protocol_version": 1,
  "last_updated": "ISO timestamp",
  "agents": {
    "<agent-name>": {
      "status": "idle|busy|blocked",
      "current_task": "description",
      "last_active": "ISO timestamp",
      "working_on": ["file1.cs", "file2.cs"]
    }
  },
  "file_locks": {
    "path/to/file.cs": {
      "locked_by": "agent-name",
      "locked_at": "ISO timestamp",
      "purpose": "reason for lock",
      "status": "in_progress|complete"
    }
  },
  "shared_files": {
    "path/to/file.cs": {
      "working_agents": ["agent-alpha", "agent-beta"],
      "merge_required": false,
      "last_merge": "ISO timestamp"
    }
  },
  "pending_merges": [
    {
      "file": "path/to/file.cs",
      "agents": ["agent-alpha", "agent-beta"],
      "status": "pending|resolved"
    }
  ],
  "agent_notes": {
    "<agent-name>": "notes for other agents"
  }
}
```

## Workflow Summary

```
┌─────────────────────┐
│ 1. git pull --rebase │
└──────────┬──────────┘
           ▼
┌──────────────────────┐
│ 2. Claim file(s)     │
│    coordinate.sh     │
│    claim <agent> <f> │
└──────────┬───────────┘
           ▼
┌──────────────────────┐       ┌──────────────────────────┐
│ 3. Make changes      │──────▶│ 4. push immediately      │
│    to claimed files  │       │    coordinate.sh push    │
└──────────────────────┘       └──────────┬───────────────┘
                                          ▼
                      ┌──────────────────────────┐
                      │ 5. Release file when done │
                      │    coordinate.sh release  │
                      └──────────────────────────┘
```

## Checklist for Every AI Agent

Before starting any task:
- [ ] Run `git pull --rebase`
- [ ] Check `status.json` for existing locks
- [ ] Claim the files you need
- [ ] Check `agent_notes` for messages from other agents

After every change:
- [ ] Run `coordinate.sh push` immediately
- [ ] Do NOT batch multiple changes into one push

When done with a file:
- [ ] Run `coordinate.sh release`

When blocked by another agent:
- [ ] Leave a note in `agent_notes`
- [ ] Check back after they push their changes

## Merge Conflict Resolution Policy

When two agents modify the same file, both sets of changes must be preserved. The correct approach:

1. **Same file, different sections**: Keep both. Git will auto-merge cleanly.
2. **Same file, overlapping sections**: Manually edit to include both contributions. Add comments noting both agents' contributions if needed.
3. **Same function modified by both**: Create overloads, merge logic, or restructure to accommodate both changes. Never delete one agent's work in favor of another's.
4. **If truly incompatible**: Leave a note in `agent_notes` and escalate via the status file.

## Troubleshooting

### Push rejected because remote has new commits
```bash
git pull --rebase
git push
```

### Accidentally claimed the wrong file
```bash
bash .ai-coordination/scripts/coordinate.sh release <agent-name> <file-path>
```

### Another agent is not releasing a file
Check their `last_active` timestamp. If stale (>30 minutes), you may:
1. Leave a note in `agent_notes`
2. If no response after 2 more pushes, claim the file yourself (override)
