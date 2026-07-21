# Runbook — M4a stage 1 live test (recipient-scoped durable delivery)

2026-07-18. Written for a single operator session. Take it in order; each phase has
a stop condition. **Phase 0 is already green** — start at Phase 1.

## What you are proving

The F1 property, live: **an enrolled consumer still drains the frozen producer's
recipient-less envelopes.** That is the regression that nearly shipped — enrollment
consumers resolving to their own empty partition while the frozen 0.5.31 mod files
everything under `legacy`.

Phase 6 is the negative control: flip `ValheimQueue:ProducerEmitsRecipients` on and
the same poll goes empty *by design*, which is exactly why the flag stays off until
the stage-3 mod cut ships.

## Ground truth (verified 2026-07-18, do not assume otherwise)

| Fact | Value |
| --- | --- |
| Live base URL | `http://8.231.129.249:42317` |
| **Not** the base URL | `http://8.231.129.249:4000` — no firewall rule, connection refused |
| VM state right now | `TERMINATED` (stopped) — Phase 1 starts it |
| External IP | `8.231.129.249` — static, survives the stop |
| Currently deployed image | `lumberjacks-gateway:m1-clean-20260717-r1` |
| **M4a code deployed?** | **No.** The recipient work is implemented and undeployed |
| Authoritative window | `p7-primary-v1` |
| Test window (use this) | `m4a-live-test-v1` — keeps production state untouched |

GCP console:
<https://console.cloud.google.com/compute/instancesDetail/zones/us-west1-b/instances/comfy-lumberjacks-p7?project=lumberjacks-exp-20260711-djc>

### Four traps that will cost you an hour each

1. **Loopback is `private-plane`.** `ValheimClientAccessMiddleware.Resolve()` checks
   the source IP *first* — loopback and every RFC1918 address short-circuit to
   `private-plane` before the enrollment headers are ever read. You **cannot** test
   the enrollment lane from your laptop against a local Gateway. That is why this
   runbook targets the public deployment.
2. **Run IAP SSH in the foreground.** From a backgrounded shell it never exits — the
   payload runs, the output is lost. Diagnosis is in
   `C:\work\comfy\infra\gcp\p7\scripts\run-promotion-drill.ps1:74-79`.
3. **`-Finalize` or it isn't durable.** Without it the drill leaves the candidate
   running only via a compose override that systemd does not load; the next reboot
   silently reverts the Gateway.
4. **`StrictRosterEnabled` is in-memory and reverts to off on gateway restart.**
   Any container recreate — including this promotion — silently disarms strict
   admission. Re-flip it per window afterward.

---

## Phase 0 — Local proof (already green, re-run only if you touched code)

```powershell
docker run --rm -v "C:\work\Lumberjacks:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test Game.sln --filter "Category!=Performance"
```

**Expect `520/520` passing.** Last run: 120 Contracts + 246 Simulation + 154 Gateway,
0 failed. (A Windows-host `dotnet test` reports 154 total / 152 passing — the two
roadmap path tests fail on Windows paths only. Do not fix them.)

Just the recipient surface:

```powershell
docker run --rm -v "C:\work\Lumberjacks:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test Game.sln --filter "FullyQualifiedName~ValheimRecipient"
```

### Optional — the mutation drill (proves the tests have teeth)

Break one thing, confirm the *named* tests fail, restore. From
`C:\work\Lumberjacks\docs\plan-m4a-recipient-isolation.md` §5:

| Mutation | Expected failures |
| --- | --- |
| Scope predicate — in `C:\work\Lumberjacks\src\Game.Gateway\Valheim\ValheimRecipientScopePolicy.cs`, make the enrollment branch ignore `producerEmitsRecipients` | 4: `ValheimRecipientScopePolicyTests.EnrollmentUsesServerRecipientAndIgnoresRequestedLabel`, `…EnrollmentWithoutRecipientFailsClosed`, and both N=2 / N=10 `ValheimRecipientIsolationTests.ValheimRecipientIsolation` |
| Lease expiry | 2: `ValheimRecipientLeaseTests.LeaseIsScopedAndExpiresWithoutSleeping`, `…RecipientReconnectRefreshesOnlyItsOwnLeaseAndTakeoverFollowsExpiry` |
| WAL version branch | 1: `ValheimZdoAuthoritativeTelemetryTests.RecipientLessV1WalFixtureReplaysIntoLegacyBucket` |

`git -C C:\work\Lumberjacks checkout -- <file>` to restore. Re-run: back to 520/520.

---

## Phase 1 — Start the VM

```powershell
gcloud compute instances start comfy-lumberjacks-p7 --project lumberjacks-exp-20260711-djc --zone us-west1-b
```

Wait for the stack, then confirm:

```powershell
Invoke-RestMethod "http://8.231.129.249:42317/health"
```

**Stop condition:** `status` = `ok`, `service` = `gateway`. If it times out, the
compose stack hasn't come up — check with the SSH command in Phase 4.

---

## Phase 2 — Cut and build the release

**Release id must match `^m\d+-[a-z0-9]+-\d{8}-r\d+$`.** Note `m4a` is **rejected**
(`\d+` won't take the `a`). Use `m4`:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\New-GatewayReleaseCut.ps1 `
    -ImageReleaseId     m4-clean-20260720-r1 `
    -AdmittedModRelease m1-clean-20260717-r1
```

> **CORRECTED 2026-07-20 — do not use `New-ReleaseCut.ps1` here.** This runbook
> originally called the both-sides cut. That script rewrites `ComfyNetworkSense.cs`'s
> `ReleaseId` const and **rebuilds the frozen mod**: same source, new hash, new id — and
> every guest package already handed out is then pinned to an artifact that no longer
> exists, holding a mod this Gateway would refuse. This is a Gateway-only promotion, so
> it needs the Gateway-only cut. Root cause and reasoning: comfy `1bc7478`.

**Two ids now, and they are supposed to differ.** `image_release_id` is what this Gateway
image *is*; `admitted_mod_release` is what it *admits*, and stays pinned to the frozen
0.5.31 artifact across many Gateway cuts. A reviewer seeing `m4-…` admitting `m1-…` is
looking at a correct cut, not a mistake. The invariant is not "the ids match" — it is
"the id baked into the shipped image equals the release we intend to admit."

**Release id must still match `^m\d+-[a-z0-9]+-\d{8}-r\d+$`** — `m4a` is rejected
(`\d+` won't take the `a`). The mod stays frozen at 0.5.31, so `deploy-network-sense.ps1`
is **not** part of this promotion.

No manual `docker build` step. `New-GatewayReleaseCut.ps1` builds the image and then
proves the baked id **from the image itself** via `Test-GatewayImageRelease.ps1`. That
indirection is the whole point: the old cut verified against
`src/Game.Gateway/bin/Release/**/Game.Gateway.dll`, a local publish that never ships. The
shipped assembly carried no release attribute, `"dev"` mapped to null, and null *disabled*
the gate — so the cut passed while the deployed Gateway admitted anything.

> The release manifests record this as `-t lumberjacks-m0-clean:a7c47b5`, and the m1
> manifest carried that m0 string unchanged. Tag with the release id as above; see
> the retag note in Phase 3.

Then write the manifest JSON under `C:\work\Lumberjacks\docs\roadmap\`, modelled on
`C:\work\Lumberjacks\docs\roadmap\m0-clean-build-candidate-r2.json`, and bundle:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\build-release-bundle.ps1 `
    -ManifestPath   C:\work\Lumberjacks\docs\roadmap\m4-clean-build-candidate-r1.json `
    -OutputRoot     C:\work\comfy\fieldlab\runs\releases\m4-clean-20260718-r1 `
    -ComfyRepo      C:\work\comfy `
    -LumberjacksRepo C:\work\Lumberjacks `
    -ModDllPath     <path to frozen 0.5.31 mod dll> `
    -GatewayImage   lumberjacks-gateway:m4-clean-20260718-r1

& C:\work\comfy\infra\gcp\p7\scripts\validate-release-bundle.ps1 `
    -ManifestPath C:\work\Lumberjacks\docs\roadmap\m4-clean-build-candidate-r1.json `
    -BundleRoot   C:\work\comfy\fieldlab\runs\releases\m4-clean-20260718-r1
```

**Stop condition:** validate returns `status = 'valid'`. Both repos must be clean and
at manifest HEAD or the bundle step refuses.

---

## Phase 3 — Promote

Rehearse first (no VM contact):

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
    -ManifestPath C:\work\Lumberjacks\docs\roadmap\m4-clean-build-candidate-r1.json `
    -BundleRoot   C:\work\comfy\fieldlab\runs\releases\m4-clean-20260718-r1
```

Then promote — **foreground shell, never backgrounded**:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\run-promotion-drill.ps1 `
    -ManifestPath C:\work\Lumberjacks\docs\roadmap\m4-clean-build-candidate-r1.json `
    -BundleRoot   C:\work\comfy\fieldlab\runs\releases\m4-clean-20260718-r1 `
    -RollbackModBackupPath /mnt/comfy-p7/backups/comfynetworksense/20260716T004955Z `
    -Execute -Finalize
```

> **Tag convention gap.** `-Finalize` pins
> `LUMBERJACKS_GATEWAY_IMAGE=lumberjacks-gateway:drill-m4-clean-20260718-r1`, but the
> VM is currently pinned to the un-prefixed `lumberjacks-gateway:m1-clean-20260717-r1`.
> Decide which you want *before* finalizing. For the clean tag, retag on the VM first
> — that step exists in neither repo:
> ```bash
> sudo docker tag lumberjacks-gateway:drill-m4-clean-20260718-r1 lumberjacks-gateway:m4-clean-20260718-r1
> ```

A benign `stdin ReadFile failed` traceback from gcloud's Windows IAP proxy after a
*successful* command is expected. Judge by exit code and explicit health/hash result.

**Rollback is a re-pin, never a rebuild:**

```bash
sudo sed -i 's|^LUMBERJACKS_GATEWAY_IMAGE=.*|LUMBERJACKS_GATEWAY_IMAGE=lumberjacks-gateway:m1-clean-20260717-r1|' /etc/comfy-p7/environment
sudo docker compose --env-file /etc/comfy-p7/environment up -d --no-build --no-deps gateway
```

---

## Phase 4 — Verify the new image is actually live

There is **no version endpoint** — `/health` reports only status/service/timestamp.
Release identity is confirmed out of band:

```powershell
gcloud compute ssh comfy-lumberjacks-p7 --project lumberjacks-exp-20260711-djc --zone us-west1-b --tunnel-through-iap `
  --command='sudo docker inspect comfy-lumberjacks-p7-gateway-1 --format "{{.Image}}"; grep -E "^LUMBERJACKS_GATEWAY_IMAGE=" /etc/comfy-p7/environment; sudo docker ps --format "{{.Names}}\t{{.Status}}"'
```

**Stop condition:** the running image id matches your manifest's `gateway.image_id`
*and* the durable pin in `/etc/comfy-p7/environment` names your new release. Both, or
the promotion is not durable.

Then re-arm strict admission (trap #4) before admitting anyone real.

---

## Phase 5 — The live F1 proof

### 5a. Seed a frozen-producer envelope

`POST /receipts` requires the `Producer` capability, which public callers never get —
so seed from the VM over loopback, where you are `private-plane`. Note the envelope
has **no `recipient_id`**: that is the frozen 0.5.31 producer's exact shape.

```powershell
gcloud compute ssh comfy-lumberjacks-p7 --project lumberjacks-exp-20260711-djc --zone us-west1-b --tunnel-through-iap `
  --command='curl -s -X POST http://127.0.0.1:4000/valheim/zdo-redirect/receipts -H "Content-Type: application/json" -d "{\"window_id\":\"m4a-live-test-v1\",\"source\":\"m4a-live-test\",\"envelopes\":[{\"seq\":900001,\"body_b64\":\"AA==\"},{\"seq\":900002,\"body_b64\":\"AA==\"}]}"'
```

Expect `{"ok":true,"window_id":"m4a-live-test-v1","received":2,"total":2}`.

Wire format is **snake_case** throughout (`window_id`, `body_b64`, `recipient_id`) —
set by `PropertyNamingPolicy = SnakeCaseLower` in
`C:\work\Lumberjacks\src\Game.ServiceDefaults\ServiceDefaultsExtensions.cs:50`.

### 5b. Enroll wary.fool

Mint a one-use, 24-hour invite:

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\new-player-invite.ps1
```

Open the returned `/join?t=<token>` URL in the browser where you're signed in as
**wary.fool**. Steam OpenID redirects to the callback, which yields a bootstrap
token. Spend it exactly once:

```powershell
Invoke-RestMethod -Method Post "http://8.231.129.249:42317/join/bootstrap" `
  -ContentType 'application/json' -Body '{"token":"<bootstrap token>"}'
```

Keep `enrollment_id`, `access_token`, and `recipient_id` from the response.

### 5c. Confirm you are an `enrollment` principal, not `private-plane`

```powershell
$H = @{
  'X-Lumberjacks-Enrollment-Id' = '<enrollment_id>'
  'X-Lumberjacks-Client-Key'    = '<access_token>'
}
Invoke-RestMethod "http://8.231.129.249:42317/api/v0/valheim/enrollment/me" -Headers $H
```

**Stop condition:** it returns your `recipient_id`. A `401 credentials_required` means
the headers didn't take; a success while your laptop sits on the public internet
confirms you are *not* being resolved as private-plane.

### 5d. The proof

```powershell
Invoke-RestMethod "http://8.231.129.249:42317/valheim/zdo-redirect/pending/m4a-live-test-v1" -Headers $H
```

**PASS:** `recipient_id` comes back as **`legacy`** and `envelopes` contains seq
`900001` and `900002`.

That is F1 proven live: an enrolled consumer drained a recipient-less envelope from
the frozen producer. Before the fix this returned empty and the lane was dead.

Then close the loop:

```powershell
Invoke-RestMethod -Method Post "http://8.231.129.249:42317/valheim/zdo-redirect/ack/m4a-live-test-v1" `
  -Headers $H -ContentType 'application/json' -Body '[900001,900002]'
```

Expect `acknowledged: 2`, `unknown: 0`. Re-poll → empty.

---

## Phase 6 — Negative control (flag on)

This confirms *why* the flag stays off. It is a compose edit, not just an env-file
edit: `/etc/comfy-p7/environment` is injected into the `docker compose` process, and
nothing forwards it into the container without a `${...}` reference.

In `C:\work\comfy\infra\gcp\p7\docker-compose.yml`, gateway `environment:` map:

```yaml
      ValheimQueue__ProducerEmitsRecipients: "${VALHEIM_QUEUE_PRODUCER_EMITS_RECIPIENTS:-false}"
```

Commit in the Comfy repo, `git pull` at `/opt/comfy` on the VM, then:

```bash
sudo sed -i '$a VALHEIM_QUEUE_PRODUCER_EMITS_RECIPIENTS=true' /etc/comfy-p7/environment
sudo docker compose --env-file /etc/comfy-p7/environment up -d --no-build --no-deps gateway
```

Re-seed 5a (the recreate clears in-memory state), then re-run 5d.

**Expected: `recipient_id` is your own opaque id and `envelopes` is EMPTY.** The
enrollment partition exists and is empty because the frozen mod emits no recipient.
That is the designed behavior and the reason for the default. Set it back to `false`
and recreate before leaving.

> This phase recreates the container twice — re-arm `StrictRosterEnabled` after.

---

## Phase 7 — Stop the VM

```powershell
gcloud compute instances stop comfy-lumberjacks-p7 --project lumberjacks-exp-20260711-djc --zone us-west1-b
```

The static IP `8.231.129.249` is retained.

---

## Cleanup

The test window is isolated from production, so this touches nothing real:

```powershell
gcloud compute ssh comfy-lumberjacks-p7 --project lumberjacks-exp-20260711-djc --zone us-west1-b --tunnel-through-iap `
  --command='curl -s -X POST http://127.0.0.1:4000/valheim/zdo-redirect/reset/m4a-live-test-v1'
```

Never `POST /valheim/zdo-redirect/reset` unqualified — it clears every window.

## Reference

- Plan: [docs/plan-m4a-recipient-isolation.md](plan-m4a-recipient-isolation.md)
- ADR: [docs/adrs/0020-recipient-scoped-durable-delivery.md](adrs/0020-recipient-scoped-durable-delivery.md)
- Review thread: [docs/handoffs/AGENT-QUESTIONS.md](handoffs/AGENT-QUESTIONS.md)
- Promotion drill: `C:\work\comfy\infra\gcp\p7\PROMOTION-DRILL.md`
- P7 compose: `C:\work\comfy\infra\gcp\p7\docker-compose.yml`
- Published M0 evidence:
  <https://github.com/djcdevelopment/comfy/blob/433f1cc33605561ae1287db9cd8f37125d795c5d/fieldlab/evidence/p7-gold-run-20260716-011112-authoritative-priority-cutover/PUBLICATION.md>
