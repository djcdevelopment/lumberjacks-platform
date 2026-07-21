# The mod config surface — decisions

Complete inventory of all 107 `ConfigEntry` keys in
`network/mod/ComfyNetworkSense/Config/PluginConfig.cs`, grouped into the decisions that
would actually be taken as a unit. Produced 2026-07-21 from three gemini-pro passes over
the file, with every load-bearing claim re-checked against the code before it was written
down here.

The frame: most of this surface was built for an **independent-agent** regime — an AI
agent running unattended, plus fleets of headless swarm clients. The operator now works
**in the seat**, with two Steam accounts he controls. The question for each group is
whether it still earns its place.

Each decision carries a counter-reason on purpose. If the counter-reason is weak, that is
itself information; where it is strong, the decision is marked to defer.

---

## D1 — The swarm / unattended-client harness · **DONE 2026-07-21**

Removed: 19 keys, `AutoCharacterSelectPatches.cs`, `MatrixCheckinRunner.cs`, and the two
auto-rehearsal wrappers. Config surface went 107 → 88 keys; mod builds 0 warnings. Recovery
pointer and the full counter-argument are in
`network/mod/ComfyNetworkSense/SWARM-HARNESS-REMOVED.md`.

**Two corrections found while cutting**, both to this document's own grouping:

- `RouteGodFlySafeguard` was listed here as swarm machinery. It is not — it guards the
  **manual** `network_sense_rehearsal` route walk from killing the character on a
  post-teleport fall. Kept.
- The manual rehearsal command shares `TryStartRehearsal` / `RunTeleportRoute` with the auto
  path. Only the automatic wrappers were removed; the operator command stays, and the
  now-dead `initiatedByAuto` / `autoRehearsal` parameters were collapsed out of both
  signatures.

So the group was 19 keys, not 20, and the deletion was narrower than "delete AutoRehearsal".

**Keys (19):** `AutoJoin*` (9), `AutoRehearsal*` (5), `CoupleAutoRehearsalToNetcodeProbe`,
`Matrix*` (4)

**Reason.** This group names its own purpose. `AutoJoinEnabled` and `MatrixCheckinEnabled`
both read *"Intended for private lab/swarm clients only"*; `AutoJoinDeriveFromHostname`
exists *"so a swarm sharing one config still spreads across distinct characters"*;
`CoupleAutoRehearsalToNetcodeProbe` exists so captured traffic exists *"without a human
hand-walking the route"*. Every one of those sentences describes a regime that ended. It
is the single largest block of config that cannot serve an operator who joins the game
himself.

**Counter-reason.** This is the only ready-made multi-client harness in the repo. If
proving a Lumberjacks optimisation under scale ever needs 20 simultaneous clients, the
hostname-derived character spreading and the matrix checkout/report loop are genuinely
fiddly to rebuild — that is a real capability being dropped, not just code.

**Reversibility.** Trivial via git; it is a clean subsystem.

**Sequencing.** Do not remove while the two-client isolation gate (M4b) is in flight.
Close that gate first, then delete.

---

> ## ⚠ Revision 2026-07-21 (later the same day) — D2 and D3 were re-examined before execution and
> **both were withdrawn.** Read this before acting on anything below.
>
> These decisions were written from audit output. Audit output describes *files*; it does not
> describe *couplings*. Every one of the deletions turned out to be entangled with something the
> audit had no reason to mention, and two of them were invalidated by the landmark design written
> hours later:
>
> | Was | Now | Why |
> |---|---|---|
> | D2 delete `LumberjacksPriorityProbe*` | **keep** | It writes the priority manifest the [landmark reach design](../../Lumberjacks/docs/network/landmark-reach-design.md) depends on. |
> | D2 delete `LumberjacksShadow*` | **keep** | Entangled with `RunTeleportRoute` via `shadow_route` — the *manual* route walk deliberately kept when the swarm harness went. |
> | D2 delete `LumberjacksProjection*` | **keep** | It renders "local-only Unity primitives without ZNetView or ZDO ownership", already branching `structure` → Cube. That is precisely the far-field proxy mechanism the landmark design needs, and the only prior art for it in the repo. |
> | D3 delete `NetcodeProbeAutoStart*` | **defer with D4** | `TryDriveNetcodeProbeAuto` is the *arming path* for the ownership observe and pin runners, which D4 defers. D3 and D4 are not independent. |
>
> D5 and D7 **were** executed — see their sections. The lesson is recorded as `L-2026-07-21-19`:
> a recommendation is a snapshot of what was known; executing it later without re-reading is how a
> considered decision becomes a mistake.

## D2 — ~~Evidence-only Lumberjacks scaffolding~~ · **WITHDRAWN, keep all 17**

**Keys (17):** `LumberjacksProjection*` (6), `LumberjacksShadow*` (5),
`LumberjacksPriorityProbe*` (3), `LumberjacksProbeInputCount`, `LumberjacksRegionId`,
`LumberjacksEventLogUrl`

**Reason.** Largest win for the least live risk. Every one of these feeds a measurement
artifact rather than a change in what the game or gateway does: projection spawns local-only
debug primitives, shadow authority computes drift without applying a single correction,
priority probe classifies nearby objects into a log. None sits on the serving path.

**Counter-reason.** The shadow runner is the only instrument that mathematically compares
Valheim motion against authoritative motion. If a physics or coordinate-translation
regression appears after the cutover, that comparison is exactly the tool you would want,
and deleting the config means deleting the visualisations behind it.

**Reversibility.** Trivial via git.

---

## D3 — Probe auto-start automation · **DELETE the automation, KEEP the probe**

**Keys (4):** `NetcodeProbeAutoStartEnabled`, `NetcodeProbeAutoStartDelaySeconds`,
`NetcodeProbeAutoStopSeconds`, `NetcodeProbeMaxDetailRows`

**Reason.** The automation answers "start capturing 25 seconds after a peer connects and
stop 150 seconds later, with nobody present". An operator in the seat starts the probe when
he wants it. `NetcodeProbeMaxDetailRows` is a genuine output cap and should survive on its
own merits.

**Counter-reason.** Automated windows are reproducible in a way a human pressing a key is
not; comparing captures across boots gets sloppier without a fixed window.

**Reversibility.** Needs rework — the peer-count watcher that fires the probe has to come
out cleanly.

---

## D4 — P3/P5 lab experiments · **DEFER**

**Keys (12):** `ZdoInjection*` (8), `OwnershipObserveEnabled`, `OwnershipPinEnabled`,
`OwnershipPinAutoCaptureMax`, `OwnershipPinPrefabs`

**Reason to delete.** These served the ownership-seizure and synthetic-fixture gates, both
closed. The code behind them is the most invasive in the mod — Harmony prefixes that skip
vanilla ownership transfer, and a poller that materialises synthetic ZDOs into a live scene.

**Counter-reason.** Strong enough to defer. The injection pipeline is the only way to feed
the client a chosen ZDO without a server producing it — i.e. the only deterministic
client-side deserialization test that exists. If the wire format changes, that scaffolding
gets rebuilt.

**Reversibility.** Needs rework, bordering on one-way: the config is the small part, the
Harmony patches are the real removal.

**Recommendation.** Leave until after the two-client gate. The ownership pin in particular
is referenced by ADR 0001 and the I2 evidence; retiring it deserves its own decision.

---

## D5 — The `ActiveSeconds` auto-disarm timers · **DONE 2026-07-21**

`zdoRedirectActiveSeconds` and `handshakeResponderActiveSeconds` now default to **0** (no cap),
matching the production posture the P7 runbook pins. The finite lab window is opt-in. This removes
the real risk: a VM configured from defaults would previously have auto-disarmed the redirect 90 s
into a session and resumed native sync, with the safe value existing only in a runbook paragraph.
`zdoInjectionActiveSeconds` was left at 90 — it belongs to D4, which is deferred, and is not on the
serving path.

Original reasoning follows.

## D5 — The `ActiveSeconds` auto-disarm timers · **KEEP the mechanism, FLIP the default**

**Keys (4):** `ZdoRedirectActiveSeconds`, `HandshakeResponderActiveSeconds`,
`ZdoInjectionActiveSeconds`, and the pattern generally

**What was checked.** An audit pass claimed these were live time-bombs that would drop the
serving path 90 seconds into a session. They are not. `ZdoRedirectRunner.cs:226-227` reads

```csharp
float activeSeconds = Math.Max(0.0f, PluginConfig.ZdoRedirectActiveSeconds.Value);
_stopAtTime = activeSeconds > 0.0f ? Time.time + activeSeconds : -1.0f;
```

so **0 means no cap**, which `handshakeResponderActiveSeconds`' own description states
outright (*"0 runs until the probe stops"*), and `infra/gcp/p7/README.md:101` pins
`zdoRedirectActiveSeconds = 0` for production. The live deployment is correctly configured.

**The residual risk is real but different.** The *default* is 90 seconds — a lab value.
The safe production value exists in a runbook line and in a `.cfg` file that lives only on
the VM; **no reference production config is tracked in this repo**, so nothing in version
control enforces it. A rebuilt VM configured from defaults would silently resume native
sync 90 seconds in.

**Recommendation.** Keep the timers — they are the hands-free rollback rehearsal that
proved P4 step 11 — but change the **default** to `0` and make the finite window the
opt-in. That inverts the risk without losing the capability.

**Counter-reason.** The 90s default is what makes an unconfigured lab run produce a
rollback rehearsal for free; defaulting to 0 means a future experimenter has to know to set
it. That is a real, if small, loss of a good accident.

**Reversibility.** Trivial via git.

---

## D6 — Serving-path flags stay OFF by default · **NO CHANGE** (rejecting a recommendation)

**Keys (4):** `ZdoRedirectEnabled`, `ZdoAuthoritativeConsumerEnabled`,
`HandshakeResponderEnabled`, `ZdoRedirectPrefabs`

An audit pass recommended flipping these to `true` so the defaults reflect production
reality. **Do not.** `false` is the standing rollback by explicit design —
`ZdoRedirectRunner.cs:50` says so in as many words: *"Config flag zdoRedirectEnabled=false
is the standing rollback."* Defaulting them on means a mod dropped into any server hijacks
world sync and admission on load, and the failure mode is not a quiet one.

**Counter-reason (the case for flipping, honestly stated).** Production genuinely depends on
three flags being hand-armed. A lost or unbound config file silently reverts the server to
vanilla sync, and silence is the worst property a failure can have.

**Better answer than either default.** Track a reference production `.cfg` in the repo, so
the production posture is version-controlled and diffable instead of existing only as VM
state plus a runbook paragraph. That addresses the real risk — the posture being unrecorded
— without arming a mod on install.

---

## D7 — Description rot on the serving path · **DONE 2026-07-21**

`zdoRedirectEnabled`'s description now states plainly that it is the live serving path in
`lumberjacks-primary` mode, and why it still defaults OFF (off *is* the standing rollback). Checked
the two neighbours while there: `zdoAuthoritativeConsumerEnabled` and `handshakeResponderEnabled`
read accurately and were left alone, and the `ownershipObserve`/`ownershipPin` "private lab runs"
wording is still **correct** — those genuinely remain lab experiments under the deferred D4. Only
one key was actually rotten, not the several first assumed.

Original reasoning follows.

## D7 — Description rot on the serving path · **FIX, regardless of everything above**

`zdoRedirectEnabled` still describes itself as *"Intended for private lab runs on the
dedicated server; coupled to the netcode-probe window."* It now carries production traffic.
Several neighbouring keys read the same way. The text is from the experiment era and now
actively misleads: it tells a reader that the live serving path is a lab toy.

**Reason.** Zero risk, zero behaviour change, and it removes the single most misleading
thing in the file.

**Counter-reason.** None material.

---

## D8 — Credentials in plaintext BepInEx config · **PROMOTE, low priority**

**Keys (2):** `LumberjacksTelemetryKey`, `LumberjacksClientAccessKey`

Both default empty, both live in a generated `.cfg` that is easy to zip up and share. Empty
is the current production posture (the private tunnel needs neither), so nothing is exposed
today.

**Counter-reason.** None material — but also no urgency, precisely because they are empty.

---

## Keep without qualification

The HUD and shortcut surface (11 keys), the perf-probe thresholds (7), the portal and
spawner connection caches (4), the core sampling knobs, and the Lumberjacks identity keys
(`GatewayUrl`, `CutoverMode`, `AuthoritativeWindowId`, `EnrollmentManifestId`,
`EnrollmentId`). The first group is the in-the-seat operator's instrumentation — it got
*more* valuable when the regime changed, not less. The last group is the live path.

## Tally

| Decision | Keys | Call |
|---|---|---|
| D1 swarm harness | 19 | **done 2026-07-21** — 107 → 88 keys |
| D2 evidence scaffolding | 17 | **withdrawn** - all three groups load-bearing |
| D3 probe automation | 3 | **deferred with D4** - it arms D4's runners |
| D4 P3/P5 experiments | 12 | defer |
| D5 auto-disarm timers | 4 | **done 2026-07-21** - redirect + handshake defaults now 0 |
| D6 serving-path flags | 4 | no change; track a reference .cfg |
| D7 description rot | — | **done 2026-07-21** |
| D8 credentials | 2 | promote, low priority |

D1 removed 19 keys (107 to 88). D2 and D3 were withdrawn on re-examination, so the remaining removable surface is far smaller than first estimated - which is the honest outcome, not a failure.
