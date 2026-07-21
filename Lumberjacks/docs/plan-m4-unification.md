# Plan — M4 unification (local proof ↔ remote deployment)

2026-07-20. Joins two halves that were built independently and have never met.

## 1. Why this document exists

**Half A — the local slice, proven.** On 2026-07-19
`Invoke-ComfyLumberjacksIntegration.ps1` closed the full correlated runtime seam:
a real dedicated Valheim server, an isolated Gateway, and a temporary real
Steam-authenticated headless client, all in Docker on the operator workstation.
Fourteen iterations; the last two PASS with the eight-step sequence
`server_candidate → importance_allowed → comfy_submitted →
gateway_authenticated_private_plane → gateway_admitted_release →
gateway_routed_legacy → consumer_processed → comfy_observed_result`. Positive:
1826 receipts, 560 acknowledged, `importance_class player_critical`, consumer
result `applied`. Negative: Importance-rejected work proven absent from both
Gateway and consumer. Audit verdict `PASS_WITH_DOCUMENTED_LIMITATIONS`, 11
checks, 0 failures (`fieldlab/integration/evidence/20260719-172149/`).

**Half B — the remote deployment, written but never executed.**
[runbook-m4a-stage1-live-test.md](runbook-m4a-stage1-live-test.md) deploys the
M4a recipient-isolation Gateway to P7 and proves the F1 property live against
the public endpoint. It has never been run. The VM is `TERMINATED`; the M4a code
is implemented and undeployed.

**What neither proves, and why the gap is structural — not laziness.**
`ValheimClientAccessMiddleware.Resolve()` checks the source IP *first*: loopback
and every RFC1918 address short-circuit to `private-plane` before the enrollment
headers are read. So Half A exercised the complete game runtime but was
*incapable* of testing enrollment identity — every actor in it resolved to
`private-plane`. Half B can test enrollment identity because the caller is on
the public internet, but it seeds synthetic envelopes over curl and exercises no
game runtime at all.

Neither is a superset of the other. The asymmetry is enforced by the middleware,
so no amount of re-running either harness closes it.

## 2. What is true today

| Capability | Proven where | Proven how | Gap |
| --- | --- | --- | --- |
| Correlated ZDO delivery | Local | Real `RPC_ZDOData` apply + ACK, 1826/560 | Only ever over `private-plane` |
| Importance gating | Local | Rejected work absent from Gateway *and* consumer | Unproven remotely |
| Release admission | Local | Schema-2 `mod_release` match; mismatch → 409 | Unproven against the deployed image |
| Enrollment identity | Nowhere | — | Half B would be the first |
| Recipient isolation | Unit only | N=2 / N=10 theories, `ValheimRecipientIsolationTests` | No live exercise; flag off |
| Two-consumer safety | Unproven | — | Blocked on stage-3 producer work |
| Rollback | Both | Harness restore + `LUMBERJACKS_GATEWAY_IMAGE` re-pin | Adequate |

## 3. The unification target

> One real Valheim ZDO, produced by a real dedicated server, crossing a
> **remote** Gateway, admitted by baked release identity, drained by a **real
> enrolled** consumer over the public internet, applied through `RPC_ZDOData`,
> and acknowledged — with the Importance-rejected path proven absent.

That single sentence is unprovable today and provable at the end of Stage 2. It
closes the `private-plane` asymmetry, and it is the first trace in which every
component is the one that would serve a volunteer.

## 4. The constraint that shapes the path

The obvious unification — point the local harness at the remote Gateway — **does
not work.** Per the runbook §5a: `POST /receipts` requires the `Producer`
capability, *"which public callers never get."* A dedicated server sitting on the
operator's LAN is a public caller to P7 and would be refused. Producing
therefore requires being on the Gateway's loopback.

P7 already satisfies this: the compose stack co-locates the Valheim server and
the Gateway on the same VM, so the server produces over `127.0.0.1` and resolves
to `private-plane` exactly as intended.

**So unification runs the producer remotely and the consumer locally — the
inverse of the instinct.** What Half A contributes is not its server; it is the
*technique*: the headless Steam-authenticated client container, which becomes
the real enrolled consumer connecting inbound to P7.

Granting a public Producer capability is explicitly **not** on this path. It
would put ZDO ingress on the public internet ahead of TLS and fail-closed
admission.

## 5. Sequenced path

Each stage is independently valuable and independently revertible.

### Stage 1 — Execute the runbook, close F1 live
- **Goal:** M4a Gateway deployed to P7; an enrolled consumer proven to drain the
  frozen producer's recipient-less envelopes.
- **Preconditions:** VM started. Base URL is `http://8.231.129.249:42317` —
  **not** `:4000`, which has no firewall rule and refuses connections.
- **Do:** [runbook-m4a-stage1-live-test.md](runbook-m4a-stage1-live-test.md)
  unchanged, Phases 1–7. Phase 2 uses `New-GatewayReleaseCut.ps1`
  (`-ImageReleaseId m4-clean-20260720-r1 -AdmittedModRelease m1-clean-20260717-r1`).
- **Stop:** Phase 5d returns `recipient_id: legacy` with seq 900001/900002; Phase
  6 returns an empty enrollment partition; ACK reports `acknowledged: 2,
  unknown: 0`.
- **Rollback:** re-pin `LUMBERJACKS_GATEWAY_IMAGE=lumberjacks-gateway:m1-clean-20260717-r1`
  in `/etc/comfy-p7/environment` and recreate the gateway service. Never rebuild.

### Stage 2 — Unification: real enrolled consumer against P7
- **Goal:** the §3 sentence, proven.
- **Preconditions:** Stage 1 green. `StrictRosterEnabled` re-armed (it reverts to
  off on every container recreate). One enrollment minted via
  `new-player-invite.ps1` and spent once.
- **Do:** run the P7 Valheim server so it produces real candidates over loopback;
  run the harness's headless client container **locally**, pointed at
  `http://8.231.129.249:42317`, authenticating with real
  `X-Lumberjacks-Enrollment-Id` / `X-Lumberjacks-Client-Key` headers rather than
  falling through to `private-plane`. Assert the same eight-step sequence the
  local harness already asserts, plus a ninth fact: the consumer resolved as
  `enrollment`.
- **Stop:** one correlation id traced from a real server candidate to a real
  `RPC_ZDOData` apply, with `/enrollment/me` confirming the consumer was never
  `private-plane`, and the Importance-rejected correlation absent from both
  Gateway and consumer logs.
- **Rollback:** harness cleanup restores local state; revoke the enrollment;
  Stage 1's re-pin still applies.
- **Note:** this is the first stage requiring harness change —
  `Invoke-ComfyLumberjacksIntegration.ps1` currently assumes it owns the server.

### Stage 3 — Producer recipient emission (Comfy, stage-3)
- **Goal:** the mod populates `recipient_id` per peer, so the isolation partition
  can actually fill.
- **Preconditions:** Stage 2 green. Work is in `C:\work\comfy`, mod side.
- **Do:** emit `recipient_id` on the schema-2 envelope per destination peer. This
  is a mod change, so it needs a **both-sides** cut and unfreezes 0.5.31 —
  budget for reissuing guest packages.
- **Stop:** schema-2 payloads carry a populated `recipient_id`; existing
  schema-1 rollback path still admitted unconditionally.
- **Rollback:** git revert in comfy; the frozen mod remains deployable.

### Stage 4 — Flip `ProducerEmitsRecipients`
- **Goal:** enrolled consumers drain their own partition instead of `legacy`.
- **Preconditions:** Stage 3 shipped **and deployed**. Flipping before that
  silently halts all delivery — no error, no 403, an empty queue forever.
- **Do:** `VALHEIM_QUEUE_PRODUCER_EMITS_RECIPIENTS=true`, recreate the gateway.
  Note this needs the compose `${...}` reference to exist; the env file alone
  does not reach the container.
- **Stop:** an enrolled consumer drains envelopes from *its own* `recipient_id`,
  and `FrozenProducerEnvelope_IsStillDrainedByAnEnrolledConsumer` still passes in
  both flag positions.
- **Rollback:** set `false`, recreate. Re-arm `StrictRosterEnabled` after.

### Stage 5 — Two real consumers
- **Goal:** M4b. Two enrolled clients, isolated queues, no cross-delivery.
- **Preconditions:** Stage 4 green. Two licensed Steam accounts.
- **Stop:** each recipient closes its own conservation equation; zero
  cross-delivery or cross-acknowledgement.

## 6. What must not be done

- **Do not use `New-ReleaseCut.ps1` for a Gateway-only promotion.** It rewrites
  `ComfyNetworkSense.cs`'s `ReleaseId` const and rebuilds the frozen 0.5.31 mod:
  same source, new hash, new id — and every distributed guest package is then
  pinned to an artifact that no longer exists. Use `New-GatewayReleaseCut.ps1`.
- **Do not grant a public `Producer` capability** to make Stage 2 easier. It puts
  ZDO ingress on the open internet ahead of TLS and fail-closed admission.
- **Do not flip `ProducerEmitsRecipients` before Stage 3 deploys.** Silent total
  delivery loss is the worst available failure shape (F1,
  [AGENT-QUESTIONS.md](handoffs/AGENT-QUESTIONS.md)).
- **Do not test the enrollment lane against a local Gateway.** The source-IP
  short-circuit makes the result meaningless.
- **Do not background IAP SSH.** The payload runs and the output is lost.
- **Do not assume `StrictRosterEnabled` survived a deploy.** It is in-memory and
  reverts to off on every container recreate.
- **Do not `POST /valheim/zdo-redirect/reset` unqualified.** It clears every
  window, including `p7-primary-v1`.

## 7. Open questions

- **Producer locality.** Stage 2 assumes the P7 server produces over loopback. If
  a volunteer's own server should ever produce, the Producer-capability question
  reopens and needs a real answer (mTLS? enrollment-scoped producer credential?).
  Unresolved in every current document.
- **Consumer telemetry durability.** Correlated consumer telemetry is in-memory.
  Whether WAL replay reconstructs it after a Gateway restart is unproven, and
  Stage 2's evidence depends on it surviving the window.
- **World-save integrity for remote windows.** The local harness restores from
  backups it took. No equivalent save-integrity comparison exists for a P7 window
  that mutates the real world.
- **Guest-package reissue cost.** Stage 3 unfreezes 0.5.31. How many packages are
  already distributed, and is there a re-pin path short of reissuing all of them?

## Reference

- Acceptance: `C:\work\comfy\fieldlab\integration\COMFY-LUMBERJACKS-ACCEPTANCE.md`
- Seam: `C:\work\comfy\fieldlab\integration\comfy-lumberjacks-seam.md`
- Diagrams: `C:\work\comfy\fieldlab\integration\diagrams\`
- Runbook: [runbook-m4a-stage1-live-test.md](runbook-m4a-stage1-live-test.md)
- Plan / ADR: [plan-m4a-recipient-isolation.md](plan-m4a-recipient-isolation.md),
  [adrs/0020-recipient-scoped-durable-delivery.md](adrs/0020-recipient-scoped-durable-delivery.md)
- Review thread: [handoffs/AGENT-QUESTIONS.md](handoffs/AGENT-QUESTIONS.md)
