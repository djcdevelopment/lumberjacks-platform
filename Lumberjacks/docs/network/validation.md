# Network Validation

Validation is layered. A passing unit suite does not establish load behavior, and a
load report does not establish that every production deployment exposes UDP.

## Evidence layers

| Layer | What it establishes | Primary artifacts |
|---|---|---|
| Contract tests | Bit order, round trips, message IDs, packet format | `tests/Game.Contracts.Tests/` |
| Simulation tests | Queueing, motion, hashing, grid and interest behavior | `tests/Game.Simulation.Tests/` |
| Gateway tests | Valheim gateway admission and injection behavior | `tests/Game.Gateway.Tests/` |
| Smoke scripts | Session, join, movement, resume, multiplayer flows | `scripts/test-*.js` |
| Load script | Concurrent clients, channel selection, update throughput | `scripts/load-test-dual-channel.js` |
| Recorded results | A dated interpretation of one test environment | `docs/load-test-dual-channel-results.md` |
| Audits | Architecture compared with explicit examination criteria | simulation and thesis audits |

## Recorded Valheim authoritative-priority result

The 2026-07-16 P7 window is the first clean live proof that one enrolled Valheim
client consumed its complete observed ZDO window through Lumberjacks priority
delivery. Session `20260716-011112-f7e72fb4` closed 83,220 Gateway receipts with
83,220 acknowledgements, zero pending, 100% priority metadata, zero native-only
delivery, and zero rejects, duplicates, retries, poll failures, acknowledgement
failures, or telemetry failures.

This result establishes single-client ZDO delivery authority only. It does not
establish replacement of Steam login, Valheim simulation, native candidate relevance
selection, non-ZDO RPCs, or recipient isolation under multiple clients. See the
[P7 network overview](valheim-lumberjacks-p7-overview.md) and the retained FieldLab
report at
`C:\work\comfy\fieldlab\runs\20260716-011112-valheim-lumberjacks-authoritative-priority-cutover\report.md`.

## Recorded dual-channel result

The March 27 load report describes 50 clients for 30 seconds:

| Environment | Recorded result | Interpretation |
|---|---|---|
| Local | 152,118 UDP entity updates, zero errors | UDP binding and high-rate downstream worked in that run |
| Azure Container Apps | UDP blocked; WebSocket fallback, zero errors | Degradation worked in that deployment configuration |

These numbers are historical evidence, not a continuously reproduced benchmark.
Hardware, code revision, cloud configuration, identifiers, and accounting boundaries
must accompany future comparisons.

## Size-claim discipline

Existing documents use several entity-update sizes. The current layout has 24 fixed
bytes plus a length-prefixed entity ID. With an eight-character ASCII ID, that means
33 payload bytes, 39 bytes with the binary envelope, and 47 bytes with the UDP token.
The serializer's `~19 bytes` comment is inconsistent with its own field layout.

Reports should distinguish:

- payload bytes;
- six-byte binary envelope plus payload;
- eight-byte UDP token plus frame;
- WebSocket framing;
- per-update versus per-second traffic.

`PlayerInput` is simpler: its payload is exactly five bytes and its binary envelope
plus payload is eleven bytes before physical transport framing.

## Reproduction checklist

Before publishing a new result, capture:

1. commit SHA and dirty-worktree status;
2. SDK, Godot, Node, database, and OS versions;
3. client count, duration, tick rate, and movement pattern;
4. transport actually selected by each client;
5. payload/frame/packet accounting definition;
6. errors, disconnects, and fallback events;
7. server CPU, memory, and outbound traffic where available;
8. raw output alongside the interpretation.

## Current verification caveats

The repository's main solution and test aggregation have changed over time. Treat
hard-coded test totals in prose as snapshots. Prefer commands and suite names, and
make the solution include every active test project before using `dotnet test
Game.sln` as the canonical count.
