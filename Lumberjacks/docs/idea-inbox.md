# Idea Inbox

Capture ideas here before deciding whether they belong in the roadmap or active work.

## Triage Legend

- `new`: captured but not reviewed
- `parked`: useful later, not now
- `promoted`: moved to roadmap or active work
- `discarded`: not worth current cost

## Inbox

### Idea: producer outbox for durable redirect acceptance
Status: parked
Why it matters:
The server adapter must retain an eligible per-peer revision until the Gateway confirms durable acceptance, then advance native bookkeeping exactly once.
Risk if ignored:
Producer loss between native suppression and Gateway acceptance remains outside the M4a stage-1 proof.
Risk if done too early:
It requires the stage-3 mod recipient emitter and a release cut across the sibling repository.
Promotion test:
Implement in M4a stage 3 after the Gateway partition and recipient identity are deployed and the mod can emit the server-derived recipient.

### Idea: OPEN → CLOSING → SEALED redirect run lifecycle
Status: parked
Why it matters:
A retained run lifecycle would make terminal closure and restart evidence explicit rather than inferred from reset and aggregate status.
Risk if ignored:
M3 remains responsible for retained-ledger lifecycle semantics.
Risk if done too early:
It would expand this Gateway-only partition change into M3 state-machine work.
Promotion test:
Revisit when M3's retained proof is scheduled; do not add lifecycle states to M4a stage 1.

### Idea: perception-budgeted world simulation
Status: promoted
Why it matters:
This is a core platform principle for scale and readability.
Next home:
`docs/architecture-principles.md` and protocol design work

### Idea: roads as safe travel corridors
Status: promoted
Why it matters:
Supports readable travel and protects players from accidental combat.
Next home:
MVP and later world-systems backlog

### Idea: distant settlement outlines as player art signatures
Status: promoted
Why it matters:
Preserves community creativity without full replication cost.
Next home:
Settlement proxy and client rendering plans

### Idea: community-owned edge relay and caching nodes
Status: parked
Why it matters:
Useful for scaling and community investment, but not first-slice critical.
Promotion test:
Promote after core authority and relevance systems are proven.

### Idea: managed paid offload for busy communities
Status: parked
Why it matters:
Potential future operating model, but dangerous if considered before trust boundaries are stable.
Promotion test:
Promote after relay/cache node roles are clearly defined.

## New Idea Template

Title:
Status: new
Why it matters:
Risk if ignored:
Risk if done too early:
Promotion test:
