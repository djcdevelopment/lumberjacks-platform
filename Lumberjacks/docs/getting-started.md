# Getting Started

This guide is for the first week of work on the platform.

## 1. Assemble the founding group

Minimum starting roles:
- product/technical lead
- backend lead
- client lead
- tools/content lead

Ideal additions:
- infra lead
- dedicated playtest coordinator from the mod community

## 2. Create the initial planning artifacts

Before writing feature code, create:
- `docs/product-brief.md`
- `docs/architecture-principles.md`
- `docs/mvp-scope.md`
- `docs/events.md`
- `docs/progression-dsl.md`
- `docs/adrs/0001-thin-client-platform.md`

These should be short and version-controlled.

## 3. Pick the first technical stack decisions

You do not need every answer yet, but you do need defaults.

Decide now:
- client engine
- server language/runtime
- transport approach
- primary database for world and progression state
- event log implementation
- admin web stack
- local environment strategy

Do not decide now:
- advanced sharding strategy
- final cloud vendor footprint
- final art pipeline
- all future plugin sandbox details

## 4. Stand up the monorepo skeleton

First codebase tasks:
- create the repo layout from `docs/repo-layout.md`
- add workspace package management
- add formatting, linting, and test commands
- add shared schema package
- add local environment bootstrap

Target outcome:
- one command starts the placeholder stack locally

## 5. Build a shared language for the team

Define and socialize these terms early:
- region
- broker
- authoritative node
- edge node
- proxy
- activation tier
- relevance
- event class
- safety corridor
- landmark signature

If the team uses these words loosely, the architecture will drift.

## 6. Create the first backlog

Use four swimlanes:
- Platform foundation
- Simulation and networking
- Community systems
- Operator tools

First recommended tickets:
- write product brief
- write architecture principles
- create schemas package
- create protocol package
- bootstrap simulation service
- bootstrap admin web
- bootstrap Discord bridge
- define first 15 canonical events
- create bot-player load harness

## 7. Prove the first vertical slice quickly

The first slice should prove:
- a player session can connect
- a region can load
- movement updates can flow
- one event can be emitted
- progression can react
- admin UI can display the result

Do not wait for combat, crafting, or polished world rendering before proving this loop.

## 8. Adopt an explicit review discipline

Every PR should answer:
- what state becomes authoritative here
- what event is emitted here
- what client-visible behavior changes here
- what admin or operator visibility exists here
- what happens if a node disappears here

This is how you keep the project aligned with the platform vision.
