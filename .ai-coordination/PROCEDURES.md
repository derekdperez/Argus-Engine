# AI Coordination Procedures

This file is the operational checklist for the three-agent workflow.

## Roles

### Codex

Codex is the senior model. It should work on:

- Architecture
- New features
- Project structure
- Cross-cutting refactors
- Contract/schema changes
- Final merge/conflict decisions
- Review of helper-agent output

Codex must record decisions with:

```bash
bash .ai-coordination/scripts/coordinate.sh decision codex "<decision>" <scope> <files_csv>
```

### Agent Alpha

Agent Alpha is a focused implementation helper. It should work on:

- Small bug fixes
- Minor UI/component fixes
- Tests
- Targeted worker fixes
- Narrow refactors contained to claimed files

Agent Alpha must not make architecture decisions.

### Agent Beta

Agent Beta is a DevOps/procedures helper. It should work on:

- Shell/Python/PowerShell scripts
- Build/deploy diagnostics
- Smoke tests
- CI helpers
- Local setup procedures
- Logging and troubleshooting scripts

Agent Beta must not alter cloud topology, Docker Compose topology, service boundaries, secrets handling, IAM/firewall rules, or production deployment behavior without Codex approval.

## Procedure 1: New task intake

1. Pull latest:
   ```bash
   git fetch --all --prune
   git pull --rebase --autostash
   ```
2. Inspect coordination state:
   ```bash
   bash .ai-coordination/scripts/coordinate.sh status
   ```
3. Register task:
   ```bash
   bash .ai-coordination/scripts/coordinate.sh register <agent> <role> "<task>"
   ```
4. Identify exact files needed.
5. Claim each file before editing.
6. For protected files, helpers must stop and ask Codex for a recorded decision.

## Procedure 2: Small change implementation

1. Claim the file.
2. Make the smallest complete change.
3. Run the narrowest relevant validation.
4. Push immediately:
   ```bash
   bash .ai-coordination/scripts/coordinate.sh push <agent> "<area>: <summary>"
   ```
5. Release the file.

## Procedure 3: Large architectural work

Only Codex should run this procedure.

1. Claim the architecture/procedure files first.
2. Record decision before broad implementation:
   ```bash
   bash .ai-coordination/scripts/coordinate.sh decision codex "<architecture direction>" architecture <files_csv>
   ```
3. Split work into small commits.
4. Avoid touching helper-owned files unless necessary.
5. If helper work conflicts with architecture, record why and preserve what can be preserved.

## Procedure 4: DevOps/script work

1. Agent Beta claims scripts/docs first.
2. Keep scripts idempotent and safe.
3. Include dry-run or preflight modes when possible.
4. Do not embed secrets.
5. Do not hard-code cloud resource deletion or destructive actions without explicit confirmation paths.
6. Test shell syntax:
   ```bash
   bash -n path/to/script.sh
   ```
7. Test Python syntax:
   ```bash
   python3 -m py_compile path/to/script.py
   ```
8. Push and release.

## Procedure 5: Conflict handling

1. Stop editing.
2. Run:
   ```bash
   git status --short
   git diff
   ```
3. If conflict is cleanly mergeable, preserve both changes.
4. If conflict changes behavior, helper agents must block:
   ```bash
   bash .ai-coordination/scripts/coordinate.sh block <agent> "Need Codex decision on conflict in <file>"
   ```
5. Codex records final decision.
6. Resolve conflict, build/test, push.

## Procedure 6: Work handoff

When handing work to another agent:

```bash
bash .ai-coordination/scripts/coordinate.sh note <agent> "Handoff: <what changed>; <what remains>; <files involved>; <tests run>"
```

Release only files that are safe for the next agent to claim.

## Procedure 7: Final response requirements

Every agent final response must include:

```text
Changed files:
- ...

Validation:
- ...

Coordination state:
- Locks released: yes/no
- Remaining locks: ...
- Codex decision needed: yes/no

Notes:
- ...
```

No agent may claim success without describing validation performed. If no validation was possible, state why.
