# Handoff

## New Project Root

Use `D:\work\game` as the working directory for the next session.

## What Was Done

A new monorepo scaffold was created under `D:\work\game`.

Top-level structure now exists for:
- `clients`
- `services`
- `packages`
- `plugins`
- `infra`
- `scripts`
- `tests`
- `docs`

The planning and strategy docs were copied into `D:\work\game\docs`.

## Root Files Created

- `README.md`
- `.gitignore`
- `package.json`
- `scripts/start-all.ps1`
- `scripts/check-workspace.ps1`

## Workspace Status

The scaffold validation script runs successfully:
- `D:\work\game\scripts\check-workspace.ps1`

Current root intent:
- planning is in place
- repo skeleton is in place
- no runtime stack is wired yet
- no git repo has been initialized in `D:\work\game`

## Planning Docs Available

Important docs to read first:
- `docs/README.md`
- `docs/product-brief.md`
- `docs/architecture-principles.md`
- `docs/mvp-scope.md`
- `docs/current-focus.md`
- `docs/90-day-roadmap.md`
- `docs/planning-system.md`

## Suggested Next Actions

1. Initialize a git repository in `D:\work\game` if desired.
2. Create `Sprint 01` from `docs/templates/sprint-template.md`.
3. Lock the first stack defaults:
   - client engine
   - server language/runtime
   - transport approach
   - database choice
   - admin web stack
4. Start implementing the first shared contracts in:
   - `packages/schemas`
   - `packages/protocol`
5. Bootstrap the first vertical slice around:
   - player session
   - region join
   - one canonical event
   - one progression update visible to operators

## Recommended First Build Order

1. `packages/schemas`
2. `packages/protocol`
3. `services/gateway`
4. `services/simulation`
5. `services/event-log`
6. `services/progression`
7. `clients/admin-web`
8. `clients/game-client`

## Notes For The Next Context

- Treat `D:\work\game` as the sole active project root.
- The old `D:\work\temp` workspace contains source analysis history and prior docs creation work, but the new implementation work should continue in `D:\work\game`.
- The current scaffold is intentionally lightweight so the next session can choose real technologies cleanly instead of inheriting accidental framework choices.
