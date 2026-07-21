# Planning System

This planning system is optimized for a solo founder operating with many roles.

## Purpose

The system exists to do four things well:
- capture ideas quickly
- prevent scope drift
- keep only a few items active at once
- turn decisions into durable written artifacts

## Core Rule

Ideas are cheap. Active work is expensive.

Every idea should go into the inbox.
Only a very small number of items should enter active execution.

## Workflow

### 1. Capture

Put every new idea into `docs/idea-inbox.md`.
Do not evaluate it in the moment unless it is urgent.

### 2. Triage

Review the inbox on a fixed cadence.
For each idea, choose one outcome:
- discard
- park for later
- promote to roadmap
- promote to current sprint
- convert into ADR or principle work

### 3. Decide

If the idea changes architecture, trust boundaries, or platform direction, write or update an ADR.
If the idea changes scope, update the product brief or MVP scope.

### 4. Execute

Only keep 1-3 active workstreams at a time in `docs/current-focus.md`.
Each workstream should have:
- objective
- why now
- exit criteria
- next concrete actions

### 5. Review

At the end of each work block or week, record:
- what moved
- what stalled
- what changed your mind
- what should be cut or delayed

## Operating Cadence

### Daily

- capture ideas
- update current focus
- record blockers or decisions

### Twice Weekly

- triage the inbox
- cut or defer low-value items
- update next actions

### Weekly

- review the 90-day roadmap
- update sprint status
- write down any architecture decisions made implicitly

### Monthly

- re-check product scope
- re-rank the roadmap
- decide what not to build next

## Planning Layers

### Layer 1: Vision

Stable documents:
- `docs/product-brief.md`
- `docs/architecture-principles.md`
- `docs/mvp-scope.md`

### Layer 2: Direction

Medium-volatility documents:
- `docs/90-day-roadmap.md`
- `docs/repo-layout.md`
- `docs/planning-system.md`

### Layer 3: Execution

High-volatility documents:
- `docs/current-focus.md`
- `docs/idea-inbox.md`
- `docs/templates/sprint-template.md`

### Layer 4: Decisions

Durable technical choices:
- `docs/adrs/`

## Solo-Founder Limits

To avoid overload, follow these hard limits:
- no more than 3 active workstreams
- no more than 1 architecture refactor in flight at a time
- no new subsystem without a written reason
- no polishing tasks until a vertical slice proves the loop

## What Belongs In Current Focus

Good examples:
- bootstrap monorepo and dev scripts
- define first 15 canonical events
- stand up session and region join flow

Bad examples:
- redesign all combat systems
- rethink every plugin boundary at once
- improve visuals before authority and events are proven

## Promotion Rules

Promote an idea from inbox to active work only if:
- it unlocks blocked work
- it reduces major architectural risk
- it proves the next vertical slice
- it materially reduces volunteer or operator overhead

## End-Of-Week Questions

Ask and answer these every week:
- What did I finish that changed the platform meaningfully?
- What am I carrying that should be cut?
- What decision did I make that needs to be written down?
- What is the single most important thing to prove next?
