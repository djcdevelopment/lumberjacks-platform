# ADR 0004 — Migrate the dedicated-server role to a GCP VM to lift the OOM ceiling

- **Status:** Accepted (2026-07-12)
- **Rung:** I7 / P7 (loopback integrity — the composition); binds where the live P7+ windows run

## Context

Every P7 composition attempt on `am4` was clipped mid-window by the kernel OOM-killer
(`oom_kill=9`, container `OOMKilled=true`, `MemLimit` unset). am4's 30 GB host ran ~97% used with
swap 100% full: the Valheim server (~8.5 GB on the 9.15M-ZDO `ComfyEra16` world) + the otel stack
(grafana/jaeger/prometheus ~3 GB) + shared usage left <600 MB, so when the auto-walk loaded a far
sector the server spiked and got SIGKILLed. The `Killed` / "exit 0 expected" masked it as a clean
restart for weeks. All four rungs were *individually* proven and proven to *compose* (no Harmony
collision, no mod-caused desync/crash across live windows w4/w5); the only thing missing was one
clean single-window four-for-four capture + a repeat — and that was blocked purely by host RAM, a
resource ceiling, **not** a code defect.

## Decision

**Run the P7/I7 live windows on a memory-roomy GCP VM, not am4.** The dedicated-server role (Valheim
+ ComfyNetworkSense + the Lumberjacks services) moves to `comfy-lumberjacks-p7`
(n2-highmem-8, 62 GiB RAM, us-west1-b, project `lumberjacks-exp-20260711-djc`, public
`8.231.129.249`) as one docker-compose, with the exact `am4` mod DLL and world state preserved
byte-for-byte (mod SHA `827fc6b2…c4f816d`, world `ComfyEra16`). OMEN stays the native rendered
client + fieldlab controller. The fieldlab harness is unchanged and unforked — it is pointed at the
VM via `fieldlab/scripts/set-gcp-p7-target.ps1` (sets `FIELDLAB_VALHEIM_*` + the `comfy-p7` IAP SSH
alias); server-side the mod reaches Lumberjacks over the compose network as `http://gateway:4000`.
Infra is a checked-in Terraform root at `infra/gcp/p7/`.

## Consequences

- **P7/I7 closed on the first clean window.** i7-w6 ran the full window with ~48 GiB free, swap
  unused, a clean probe stop, and no OOM — all four gates green at once (pin
  `held_with_negative_control`, redirect `receipts_match_no_loss` 3474==3474, injection
  `rendered_with_lumberjacks_owner`, handshake Lumberjacks-decided ACCEPT). Evidence
  `evidence/i7-w6/`.
- **am4 is retired from the P7 dedicated-server role** (still fine for lighter local-lab work). The
  live P7+ target is the GCP VM — see memory [[gcp-p7-live-target]].
- **When every failure is a mid-run disconnect, suspect the host before the patch.** The weeks lost to
  "the composition clips" were a RAM ceiling; no mod change would ever have fixed it. → retro lesson
  `L-2026-07-12-2`.
- **Reconcile a "someone already deployed/passed it" claim against live sources before acting.** The
  VM's existence + a prior i7-gcp-w1 window were real, but the *gate* was doc-only and the box had
  drifted armed; live `gcloud`/SSH/server-log/mod-SHA established true state. → lesson `L-2026-07-12-1`.
- **New operational surface, new traps.** The `comfy-p7` reach is a gcloud IAP tunnel: its SSH hangs
  in a background task and PS 5.1 aborts on its stderr WARNING — drive stages foreground, recover in
  bash. → memory [[iap-ssh-foreground-only]].
- **Trade-off / cost.** A running cloud VM has a standing bill and a cutover invariant (never let am4
  and GCP write the same world lineage concurrently — enforced in `infra/gcp/p7/README.md`). Justified
  because no amount of am4 tuning buys 60+ GiB, and the milestone needed it.

## Related

ADR 0003 (the mod HTTP path that runs over the compose network here); `infra/gcp/p7/README.md`;
`evidence/i7-w6/`, `evidence/i7-gcp-w1/`; `retro/SESSION-RETRO-2026-07-12.md` (lessons
`L-2026-07-12-1/-2/-3`); memory [[gcp-p7-live-target]], [[iap-ssh-foreground-only]],
[[am4-host-oom-restart]].
