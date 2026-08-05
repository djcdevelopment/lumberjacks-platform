# ADR 0017 — Acceptance proofs must exercise the lane users ship on

- **Status:** Accepted (2026-08-05)
- **Rung:** M7 / cutover acceptance; binds the C10b final gate and any future "alpha-ready" claim

## Context

P7 has three ZDO delivery lanes, each with a different auth story and a different activation
surface:

1. **Native vanilla sync** — no Gateway involvement; works for any client.
2. **Enrollment consumer lane** — the server primary-redirects the world into the Gateway's
   authoritative window; clients drain it as *enrolled consumers*. On P7 the public plane is
   credentialed, so consumer attach requires enrollment keys from the personalized mod-zip. This is
   the lane every July play session ran, and the only lane a real alpha tester will ever run.
3. **Harness journal lane** — canonical session plus private-plane runtime arming applied from the
   VM's host filesystem. This is the lane every C8/C10b candidate proof runs. The arming is
   in-memory; any container restart erases it.

On 2026-08-05 the first human 2-player session against the promoted candidate failed three
consecutive ways — dead-partition 409, session-disabled at-rest config, localhost gateway URL —
and then a fourth (`active_consumers: 0`, 4,421 receipts pending) because the lab clients hold no
enrollment credentials. Meanwhile the candidate proof machinery was *green* on this same deployment
within the hour. Nothing was wrong with the proofs on their own terms; they were exercising lane 3
while the humans were failing on lane 2. AM4 masks the entire class because its local gateway is
uncredentialed — lane 2's auth requirement simply does not exist there.

This is `L-2026-07-31-2` ("warm state masks dead lanes") recurring at architecture scale: the proof
lane is warm in exactly the sense the cold-cache rule was written to catch, but the staleness is a
whole delivery path rather than a cache.

## Decision

**No release claim ("cutover proven", "alpha-ready") may rest solely on a proof lane that
production users do not run.** Concretely:

- The C10b acceptance set gains an **enrollment-lane end-to-end proof**: starting from a fresh
  self-service mod-zip enrollment (the real onboarding artifact, not a harness config), a client
  must attach as a consumer on P7's credentialed plane and end with physically verified world
  visibility and co-presence. This proof gates alpha invitations; the candidate-proof green does
  not, by itself.
- Harness-only lanes remain legitimate for what they are — controlled instrumentation of specific
  invariants — but every proof receipt must name the lane it exercised, so a green can never be
  silently read as covering a lane it didn't touch.
- Where a lane exists only in one environment (P7's credentialed plane has no AM4 equivalent), the
  proof for it must run in that environment; an uncredentialed local pass is explicitly
  non-evidence for it.

## Consequences

- The alpha gate moves from "candidate 12 green" to "mod-zip → visible world," which is the user's
  actual first five minutes and therefore a strictly better gate.
- The enrollment lane needs harness support it doesn't have today (the client harness has no
  enrollment/consumer switch) — that tooling is part of the gate's cost, not optional polish.
- The declarative-mode fix (session-plane fix plan item 5) is a precondition for cheap lane
  labeling: a receipt can only name its lane if the effective mode is queryable.
- This generalizes ADR 0009 (verify against an independent source) from data to *paths*: a proof
  that exercises only the instrument's own lane is reading its own output at the routing level.

## Related

`fieldlab/evidence/p7-gateway-session-plane-fix-plan-20260805.md` (addendum);
`retro/SESSION-RETRO-2026-08-05.md` lessons `L-2026-08-05-1`, `L-2026-08-05-4`;
ADR [0008](0008-liveness-is-not-admission.md), [0009](0009-verify-against-an-independent-source.md),
[0016](0016-banked-state-must-carry-session-identity.md); memory `p7-three-delivery-lanes`.
