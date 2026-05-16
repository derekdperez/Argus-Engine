# AI Agent Coordination Protocol

This directory is the coordination contract for AI agents working on Argus Engine at the same time.

The goal is simple: multiple agents can work concurrently without silently overwriting each other, losing work, creating incompatible changes, or making architectural decisions in different directions.

## Current agent model

| Agent | Role | Normal work | Decision authority |
|---|---|---|---|
| `codex` | Senior architect / lead implementer | Architecture, cross-cutting refactors, new features, schema/contract changes, final merges | Final decision-maker |
| `agent-alpha` | Implementation helper | Small bug fixes, targeted component changes, tests, minor enhancements | Must follow Codex decisions |
| `agent-beta` | DevOps / automation helper | Scripts, deployment procedures, diagnostics, CI/CD helpers, local/cloud operational fixes | Must follow Codex decisions |

`codex` is the senior model and has final decision authority. If there is disagreement, ambiguity, a conflict, or an architectural question, Codex decides and records the decision with `coordinate.sh decision`.

## Non-negotiable rules

1. **Never start from stale code.** Pull before planning or editing.
2. **Never edit an unclaimed file.** Claim exact files before modifying them.
3. **Never delete, revert, reset, or overwrite another agent's work.** Preserve other changes unless Codex explicitly decides otherwise.
4. **Never use `git reset --hard`, `git clean -fd`, `git push --force`, or broad checkout/revert commands.** These are forbidden unless the human owner explicitly instructs it.
5. **Never make broad architectural changes as a helper agent.** Helper agents must stay inside the task scope or request a Codex decision first.
6. **Always push small, coherent commits.** Do not hold large local changes while other agents are working.
7. **Always release locks when finished.** Locks are a coordination tool, not ownership.
8. **Always leave notes when blocked.** Use `coordinate.sh note` or `coordinate.sh block`.
9. **Treat `.ai-coordination/status.json` as live state.** Do not manually rewrite it unless the script is broken.
10. **Before final output, state exactly what changed, what was tested, and what remains risky.**

## Start-of-session procedure

From the repository root:

```bash
git fetch --all --prune
git pull --rebase --autostash
bash .ai-coordination/scripts/coordinate.sh status
```

Then register yourself:

```bash
bash .ai-coordination/scripts/coordinate.sh register codex senior_architect "Implement architecture change X"
bash .ai-coordination/scripts/coordinate.sh register agent-alpha implementation_helper "Fix bug Y"
bash .ai-coordination/scripts/coordinate.sh register agent-beta devops_helper "Improve deployment script Z"
```

Use only your assigned agent name. Do not impersonate another agent.

## Claiming files

Claim every file before editing:

```bash
bash .ai-coordination/scripts/coordinate.sh claim <agent-name> <file-path> "<purpose>"
```

Example:

```bash
bash .ai-coordination/scripts/coordinate.sh claim agent-alpha src/ArgusEngine.CommandCenter.Web/Components/StatusDashboard.razor "Fix stale worker status display"
```

The claim action pulls latest code, checks current locks, updates `status.json`, commits the lock update, and pushes it.

## Shared files

If two agents must edit the same file, do not bypass the lock. Ask Codex to decide whether the file should be shared.

Codex records the decision:

```bash
bash .ai-coordination/scripts/coordinate.sh decision codex "Allow agent-alpha and agent-beta to both edit deployment diagnostics because the changes are independent" shared-file deployment/debug.sh
```

Then each agent runs:

```bash
bash .ai-coordination/scripts/coordinate.sh share <agent-name> <file-path>
```

Shared files require extra care. Every agent must inspect `git diff` and preserve the other agent's changes during merges.

## Pushing changes

After each coherent unit of work:

```bash
bash .ai-coordination/scripts/coordinate.sh push <agent-name> "<commit message>"
```

Commit message format:

```text
<area>: <short summary>
```

Examples:

```text
web: fix status dashboard worker count
scripts: add deployment preflight checks
engine: persist recon orchestrator provider state atomically
```

Do not batch unrelated changes into a single commit.

## Releasing files

When a file is complete and pushed:

```bash
bash .ai-coordination/scripts/coordinate.sh release <agent-name> <file-path>
```

Release files promptly. If you still need follow-up work, claim them again later.

## Heartbeats

Long-running sessions must update heartbeat at natural pauses:

```bash
bash .ai-coordination/scripts/coordinate.sh heartbeat <agent-name>
```

Use this before stepping away, after a long build/test run, or before waiting on another agent.

## Codex decision procedure

Codex records decisions when any of these are true:

- A helper agent needs to modify protected files.
- A task affects architecture, contracts, schema, deployment topology, or project structure.
- Agents disagree about the correct implementation.
- A merge conflict requires choosing between incompatible approaches.
- A helper wants to expand scope beyond the assigned task.

Command:

```bash
bash .ai-coordination/scripts/coordinate.sh decision codex "<decision summary>" <scope> <comma-separated-files>
```

Examples:

```bash
bash .ai-coordination/scripts/coordinate.sh decision codex "Use Discovery API as the owner of recon-agent endpoints; web remains API-only" architecture src/ArgusEngine.CommandCenter.Discovery.Api/Program.cs,src/ArgusEngine.CommandCenter.Web/Components/Pages/CommandCenter.razor
```

```bash
bash .ai-coordination/scripts/coordinate.sh decision codex "Agent-beta may update deploy smoke tests only; no docker-compose topology changes" devops scripts/smoke.sh,deploy.py
```

## Protected files and high-risk areas

Helper agents must not modify these without a Codex decision:

- Solution/project/package files: `*.sln`, `*.slnx`, `*.csproj`, `Directory.*.props`, `Directory.*.targets`, `Directory.Packages.props`
- Public contracts and DTOs: `src/*Contracts*/**`
- Domain model/core entity changes: `src/ArgusEngine.Domain/**`
- EF migrations and persistence schema changes: `src/**/Migrations/**`, persistence model changes that alter schema
- Gateway routing and service boundaries: `src/ArgusEngine.CommandCenter.Gateway/**`
- Application orchestration and queue semantics: `src/ArgusEngine.Application/**`, `src/ArgusEngine.Infrastructure/**/Messaging/**`
- Entrypoints and DI composition: `Program.cs`, `Startup*.cs`, service registration extensions
- Docker Compose and cloud topology: `deployment/docker-compose*.yml`, `deployment/gcp/**`, `deploy.py` when it changes resource topology
- Security-sensitive config: auth, diagnostics API keys, secrets handling, CORS, proxy routing, firewall/cloud IAM

Helper agents may normally modify these without a Codex decision, assuming they claim files first and stay scoped:

- Local diagnostic scripts in `scripts/`
- README/docs/procedural notes
- Narrow UI bug fixes that do not change contracts or API routes
- Tests for existing behavior
- Small worker bug fixes that do not change message contracts or queue ownership

## Merge conflict policy

When conflicts occur:

1. Stop and inspect conflict files.
2. Run `git status --short` and `git diff`.
3. Preserve both agents' work wherever possible.
4. If changes are incompatible, record a block and ask Codex to decide.
5. Only Codex may choose to discard another agent's work, and the reason must be recorded in `decision_log`.

Conflict markers:

```text
<<<<<<< HEAD
local changes
=======
remote changes
>>>>>>> incoming commit
```

Do not blindly accept either side.

## Status file

`.ai-coordination/status.json` is live coordination state. The script updates it using atomic JSON rewrites.

Important fields:

- `agents`: active agents, role, current task, heartbeat, and claimed files
- `file_locks`: exclusive file claims
- `shared_files`: files approved for concurrent work
- `decision_log`: Codex decisions
- `blocked_items`: current blockers
- `agent_notes`: short notes for other agents
- `role_policy`: assignment of final authority and scope restrictions

## End-of-session procedure

Each agent must finish by running:

```bash
git status --short
bash .ai-coordination/scripts/coordinate.sh heartbeat <agent-name>
```

Then release completed files:

```bash
bash .ai-coordination/scripts/coordinate.sh release <agent-name> <file-path>
```

Final response must include:

- Files changed
- Tests/builds run
- Any failures or skipped tests
- Remaining locks, if any
- Any Codex decisions needed
