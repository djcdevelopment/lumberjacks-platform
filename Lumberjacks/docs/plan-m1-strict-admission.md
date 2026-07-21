# M1 strict admission — kickoff plan

2026-07-16. M1 is the active milestone. Authoritative scope lives in
[valheim-volunteer-platform-plan.md](network/valheim-volunteer-platform-plan.md)
(Milestone 1: identity model, deliverables, Gate M1); this document maps that scope
onto the current sources and fixes an implementation order. Survey drafted via a
HEARTH-routed large-context pass over the Gateway and mod sources; every
load-bearing claim below was re-verified against the code by hand.

Standing constraint from M0: the running release is frozen and identity-pinned.
Any Gateway or mod change ships as a new release through the reproducible
bundle + promotion-drill pipeline, so stages are grouped to minimize release
cuts — especially mod cuts.

## 1. Current state

- **Enrollment storage** — `SteamEnrollmentService` persists a plaintext JSON
  file (`invites.json`); `AccessToken` is stored and compared in cleartext
  (fixed-time compare at `SteamEnrollmentService.cs:52`). No uniqueness per
  SteamID, no revocation/expiry/last-used state on the enrollment itself, no
  audit events, no release-compatibility record.
- **Credential echo** — ~~`SteamEnrollmentEndpoints.cs:57,66` returns the raw
  `AccessToken` into the browser response as `lumberjacksClientAccessKey`
  (config-snippet handoff). Reusable secret over plain HTTP today.~~
  **Closed in stage 4** (2026-07-17): the callback returns a single-use bootstrap
  code; `POST /join/bootstrap` mints the access token at consumption, so no reusable
  credential reaches the browser and none exists at rest.
- **Authorization** — `ValheimClientAccessMiddleware` accepts either the shared
  `X-Lumberjacks-Client-Key` env credential or an enrollment id+token pair, and
  grants global access; `SteamEnrollmentEndpoints` hand-checks
  `X-Lumberjacks-Admin-Key`. No admin/producer/consumer/telemetry/public split.
- **Join decision (Gateway)** — `ValheimHandshakeService.Evaluate` emulates
  native checks (banlist, password, duplicate uid, capacity
  `MaxPlayers = 10` const at `ValheimHandshakeService.cs:149`). It does **not**
  consult the enrollment roster, release identity, readiness lease, or a
  one-seat reservation.
- **Join decision (mod)** — `HandshakeResponderRunner` is deliberately
  fail-OPEN (comment at line 28): any endpoint error or unparseable verdict
  returns `HandshakeDecision.PassThrough` (`endpoint_error` at line 185,
  `unparseable_verdict` at line 212). Strict admission requires fail-closed.
- **Transport** — both the handshake responder and
  `ZdoAuthoritativeConsumerRunner` use a raw-socket HTTP client and throw
  `NotSupportedException` for any non-`http` scheme
  (`HandshakeResponderRunner.cs:233`). No TLS path exists in the mod.
- **Recipient identity** — the consumer self-assigns
  `_consumerId = Guid.NewGuid()...` (`ZdoAuthoritativeConsumerRunner.cs:80`)
  and the Gateway trusts it; the plan requires a credential-derived opaque
  `recipient_id` the client cannot choose.
- **Rate limiting** — none (`Program.cs` registers no limiter).

## 2. Gap analysis

| M1 deliverable / gate bullet | Current state | Gap |
| --- | --- | --- |
| One active enrollment per SteamID | Blind append to JSON dictionary | Uniqueness rule + explicit admin replacement |
| List/revoke/expiry/last-used/audit/compat | Only `EnrolledUtc` tracked | Expanded record + admin endpoints + audit events |
| Secrets hashed, never re-returned | **Closed** (stage 1 + stage 4): hashed at rest; the echo is a single-use bootstrap and the access token is minted on consumption | — |
| Capability split (admin/producer/consumer/telemetry/public) | Two global keys | Policy-based authorization, capability-scoped credentials |
| Join hook asks Gateway about actual joining SteamID | **Closed in stage 2**: roster keyed on `host_name`, the socket's Steam-authenticated SteamID64 (§5.3); `uid` is a session id and is not used | Flip `StrictRosterEnabled` on per window; fail-closed still needs the mod cut |
| Release/protocol compatibility gate | Protocol checked (`net_version != 36`, check A); release **not** checked — the mod sends no release identity of its own (§5.9) | Mod must send its own release identity (stage 3) + a manifest-pinned hash check, with the hash source decided |
| Fresh readiness lease | Not implemented | **Out of M1 scope** (2026-07-17) — M4a owns per-peer readiness and defines the lease; see §7 |
| One-seat capacity reservation | `MaxPlayers = 10` const, and `CurrentPlayers` is operator-configured, not observed | Gateway-enforced reservation of exactly one seat, with a passive TTL to survive the absent disconnect signal (§5.4) |
| Strict admission fails closed | Mod fail-open on any fault | Cutover-mode-aware reject on endpoint error |
| Certificate-validating TLS on public traffic | Mod throws on `https` | TLS client in mod + TLS termination at the VM |
| No credential-selected recipient | Client-chosen `consumer_id` trusted | Server-derived opaque `recipient_id` |
| Rate limits (invite/readiness/poll/ACK/telemetry) | None | Independent per-surface limiters |

## 3. Implementation stages

TLS termination: **Caddy sidecar** in the existing compose stack — automated
Let's Encrypt issuance/renewal, HTTP→HTTPS at the edge, reverse proxy to
Kestrel; container-to-container producer traffic stays on the private Docker
network. Prerequisite: a stable public DNS name for the volunteer endpoint
(ACME will not issue for a bare IP) and ACME state on a persistent mount so
drills/rebuilds don't burn issuance rate limits.

1. **Identity schema, capability split, rate limits** (Gateway only) —
   hashed-at-rest enrollment store with unique-active-SteamID, revoke/expiry/
   last-used/audit; replace the middleware with capability policies; add
   per-surface rate limiters; derive and persist `recipient_id` per enrollment.
   Verify with unit/integration tests (hashing, policy rejection matrix,
   limiter exhaustion). No mod involvement.
2. **One-seat reservation + roster gate** (Gateway only) — ships live: the one-seat
   reservation as pure capacity with a consumer-poll liveness lease (§5.4), and the
   **roster gate** keyed on the socket's Steam-authenticated `host_name` (§5.3),
   behind `StrictRosterEnabled` (default off); plus reason codes and retained
   rejection records. Landed `cd78296`, `b2a8d19`, and the roster commit — see §7.
   A first re-scope on 2026-07-16 deferred the roster to stage 3 believing no Steam
   identity reached the Gateway; that was wrong and is corrected in §5.3.
   `lease_stale` is out of M1 scope as of 2026-07-17 — M4a owns it (§7). The
   release-compatibility gate rides stage 3 whole: it is a string compare whose
   expected-value source is deferred (§5.9) and whose input the mod does not send.
   **Strict admission is real in stage 2 but opt-in and fail-open**: the frozen mod
   still passes through on any endpoint fault, so stage 3 still owns fail-closed.
3. **Mod cut: authenticated identity + fail-closed strict mode + TLS +
   server-derived recipient** (mod + Gateway, one release cut) — decode the
   SteamID64 **from the session ticket** inside the mod and send it (the mod is the
   only caller with Steamworks access, §5.5; a client-asserted field would be
   forgeable), send the mod's own release identity, and flip the stage-2 flag on;
   TLS-capable client with certificate validation; in strict cutover mode any
   endpoint fault becomes a reject (labeled native recovery stays an explicit
   operator mode); stop sending a client-chosen `consumer_id`. Verify live: Gateway
   stopped ⇒ strict mode refuses admission; the §4 `2-dark` rows re-verified on the
   wire; https endpoint exercised end-to-end; no reusable credential on a plaintext
   public link (capture check).
4. **Bootstrap handoff hardening** (Gateway only) — **LANDED 2026-07-17, undeployed.**
   Redeeming an invite now issues a single-use bootstrap instead of the config;
   `POST /join/bootstrap` exchanges it for the config exactly once. The access token is
   **minted at consumption**, not parked in the store waiting for one, so it never
   exists at rest in any form and the enrollment carries no credential until the
   installer acts (`Verify` returns `bootstrap_pending`, failing closed). POST, not
   GET, so the code cannot be spent by pasting a URL and stays out of history,
   referers, and access logs. Public and rate-limited under `join`: it is deliberately
   outside `IsGated`, because the installer has nothing to authenticate with yet.
   Store schema v2→v3, upgraded in place on first Save; v2 enrollments keep their token
   hash and keep verifying. Replay verified by mutation: disabling the single-use gate
   fails exactly `Bootstrap_IsSingleUse` and nothing else. M2 consumes this.
   ~~**Open:** an expired bootstrap strands the volunteer — the enrollment exists and
   one-active-per-SteamID refuses a second, so re-issuing needs an admin revoke plus a
   fresh invite. TTL is 24h (`LUMBERJACKS_BOOTSTRAP_TTL_HOURS`) to make that unlikely
   rather than impossible; a self-serve re-issue is the real fix and is not built.~~
   **Closed 2026-07-18 — self-serve re-issue, designed with Derek before building.**
   `GET /join/reissue` redoes the Steam OpenID sign-in — the identity root that created
   the enrollment — so no copyable artifact (least of all the expired code itself, the
   rejected alternative: it would have turned the browser artifact stage 4 neutralized
   into a long-lived re-issue credential) authorizes a re-issue. A SteamID whose Active
   enrollment is still credential-less gets a fresh code for the *same* enrollment:
   EnrollmentId and RecipientId are reused, not rotated, so nothing downstream churns.
   The prior unused code is **deleted from the store, not flagged** — a deleted record
   answers `bootstrap_invalid` under every past and future binary, so a rollback to a
   pre-re-issue gateway cannot resurrect a superseded code the way an ignored flag
   would; at most one bootstrap is live per enrollment. **Pending-only by design**: once
   the installer has minted a credential (`TokenHash` set), re-issue answers
   `already_installed` and recovery stays admin revoke + re-invite — lost-credential
   rotation is a possible later follow-up on the same mechanism, not part of this.
   Rate-limited three ways: the public `join` IP limiter, a per-enrollment cooldown
   (`LUMBERJACKS_REISSUE_COOLDOWN_MINUTES`, default 15), and a lifetime chain cap
   (`LUMBERJACKS_REISSUE_MAX_BOOTSTRAPS`, default 10) counted in a new
   `BootstrapIssueCount` enrollment field — additive on schema v3, so pre-existing
   records read 0 and get one spare re-issue, which is fine because the cap is an abuse
   bound, not a security invariant. All four guards are mutation-verified: disabling
   supersede-deletion, the cap, the cooldown, or the pending-only check fails exactly
   its own test and nothing else. The handoff text now points the volunteer at the
   re-issue URL instead of "ask the operator". Undeployed, like the rest of stage 4;
   not exercised against a real Steam callback (the OpenID verify is the same code the
   enrollment callback has always used, now shared rather than duplicated).

**Stage 3 build status — 2026-07-17.** The mod-side half is **built and committed,
undeployed**, all of it shipping OFF. Nothing below has touched P7.

- *Transport* (comfy `b7395d3`, `e09937c`, `4700b4f`): the raw-socket client is extracted
  into Unity-free `BoundedRawHttp` and shared by the handshake and the consumer — the
  socket and the bounded read only; each caller still builds its own head, because the
  consumer's credential headers come from `PluginConfig` and its port-less `Host` is what
  the Gateway has always been sent. **TLS is in**: `https` authenticates with
  `SslProtocols.Tls12` and accepts only `SslPolicyErrors.None`. Revocation is not checked —
  known and bounded (§5.1 still owes the Mono cipher proof against a real server; the TLS
  *success* path is unprovable in a unit test without a machine-trusted cert, so what is
  proven is that an untrusted cert is **refused**).
- *Release identity* (comfy `585bfac`): the handshake now sends `mod_version` and
  `mod_release_id` — the build answering, not the client joining. `ReleaseId` is a hand-set
  const beside `PluginVersion`, `"dev"` until a cut names it, per §5.9.
- *Fail-closed* (comfy `585bfac`): both fault paths — dead endpoint, unparseable verdict —
  now meet in one place, because they are one event: no verdict we can trust.
  `handshakeResponderStrictMode` defaults **false** (today's fail-open); ON rejects with
  `ErrorConnectFailed` (5), **not** `ErrorBanned` — the fault is not the player's and
  telling them they are banned is a lie they cannot act on. Leaving it off stays the
  labelled native-recovery mode §3 calls for. Not unit-tested and not pretended to be: it
  reads `PluginConfig` and logs via `ZLog`, so it cannot link into the Unity-free test
  assembly, and what it adds is one ternary — the behaviour that matters is the live check
  this stage already requires.
- *TLS termination* (comfy `a74442d`): a Caddy sidecar, digest-pinned, **inert behind the
  `tls` compose profile** — `docker compose up` cannot start it by accident. ACME state is
  on a persistent mount, which is the whole point: the drill rebuilds this stack, and a
  fresh `/data` re-issues until Let's Encrypt's 5/week duplicate limit locks TLS out for
  days.

**Still needs a human, and none of it is mod work:** a real **DNS A record** (ACME will not
issue for a bare IP — the long pole), an ACME contact address, and firewall openings for
**80** and 443 (80 is not optional; HTTP-01 needs it, and terraform opens only the player
port today).
**[2026-07-18 update: two of the three are DONE.** The DNS name exists and is recorded —
`comfy-p7.duckdns.org` → the **reserved static** `8.231.129.249` (survives the VM's
TERMINATED-between-sessions lifecycle; duckdns used as static DNS, no updater; on the
Public Suffix List so the name carries its own Let's Encrypt rate limit), verified against
public resolvers, comfy `29326eb` — which also records the browser-IP pre-fill trap that
briefly pointed the name at an operator laptop. Firewall 80/443 opened in comfy `2765ff9`.
**Remaining human input: only the ACME contact address**, deliberately kept out of this
repo (expiry warnings land there and the repo publishes evidence); it goes in
`/etc/comfy-p7/environment` on the VM at the next boot, alongside the stage-3 cut.] Then the live verifications this stage has always owed: Gateway stopped ⇒
strict refuses; the §4 `2-dark` rows on the wire; https end to end; a capture check for a
reusable credential on a plaintext link. Plus the priority-drain baseline the consumer's
new deadline is owed (§5.8) — it is a ceiling and should not move healthy timing, but that
is reasoning, not measurement.

Release cuts: stages 1–2 can ship as one Gateway release; stage 3 is the
single mod+Gateway cut; stage 4 rides the next Gateway cut. Every cut goes
through bundle validation and the promotion drill per the M0 pipeline.

## 4. Admission acceptance matrix

`Stage` records where each row is **verifiable**: `1` already landed (b842bd3); `2-live`
enforced live once stage 2 ships; `2-dark` logic built and synthetically tested in stage 2 but
unreachable on the wire until the stage-3 mod cut feeds it an identity; `3` needs the mod cut.
`recipient_forbidden` is stage 3 despite stage 1 landing it on the telemetry heartbeat
(`ValheimTelemetryHeartbeatEndpoints.cs:76`): the row's scenario is poll/ACK, where the
Gateway still trusts a client-supplied `consumer_id` (`ValheimZdoRedirectEndpoints.cs:85`)
until the mod stops self-assigning one.

| Case | Setup | Expected decision | Reason surface | Test | Stage |
| --- | --- | --- | --- | --- | --- |
| Enrolled + compatible + seat free | happy path | accept | — | auto + live | 2-live (flag) |
| Uninvited SteamID | no enrollment record | reject | not_enrolled | auto + live | 2-live (flag) |
| Revoked / expired enrollment | admin revoke; expiry passed | reject | enrollment_revoked / expired | auto | 2-live (flag) |
| Wrong Steam account | enrollment for different SteamID64 | reject | steamid_mismatch | auto + live | n/a — see below |
| Wrong mod/release | stale hash in handshake | reject | release_incompatible | auto | 3 |
| Stale readiness lease | lease older than TTL | reject | lease_stale | auto | ~~M1~~ → M4a (2026-07-17, §7) |
| Seat taken | second concurrent join | reject | capacity_reserved | auto | 2-live |
| Replayed invite / duplicate enrollment | reuse consumed invite | reject | invite_consumed | auto | 1 — enrollment surface |
| Consumer token on producer/admin/reset/compaction/handshake ops | scoped credential | deny (403) | capability_denied | auto | 1 |
| Consumer names another recipient_id | forged recipient in poll/ACK | deny | recipient_forbidden | auto | 3 |
| Gateway down, strict mode | stop gateway container | reject at join | strict_authority_unavailable | live | 3 |
| Gateway down, native recovery mode | operator-labeled mode | native decision | labeled pass-through | live | 3 |
| Plaintext public link | http:// probe of volunteer surface | no reusable credential observable | redirect/refuse | live | 3 |

**Corrected 2026-07-16.** An earlier revision marked every identity row `2-dark` on the claim that
the frozen mod sends no Steam identity. It does: `host_name` is the socket's SteamID64, and vanilla
ticket-verifies that same identity (§5.3). The roster rows are therefore **live in stage 2** behind
`StrictRosterEnabled` (default off, §7), not deferred to the mod cut.

What genuinely is not a stage-2 handshake row, and why — the matrix had been conflating three
surfaces:

- `steamid_mismatch` is **not a handshake row at all**. It means a *credential* belongs to another
  account, and the handshake carries no credential. Keyed by SteamID64 a mismatch is
  indistinguishable from `not_enrolled`, which is what the gate returns. It belongs to the
  middleware, where a credential exists to mismatch.
- `invite_consumed` is an **enrollment-surface** row (replay of a used invite); the handshake never
  sees an invite. Stage 1 enforces it at redemption.
- `lease_stale` is **out of M1 scope** as of 2026-07-17. Nothing in the system issues a readiness
  lease — no endpoint, no record, no field — because M1 has no consumer for one. M4a does: it owns
  exact per-peer readiness and reconnect/takeover, requires the lease in its own work, and tests
  lease takeover at its exit. M1 was holding the contract on M4a's behalf (§7).
- `release_incompatible` still rides stage 3 whole: the mod sends the *joining client's*
  `version`/`net_version`, never its own release identity (`HandshakeResponderPatches.cs:34,39`),
  and its expected-value source is deferred anyway (§5.9).

## 5. Risks and open questions

1. Mono/Unity TLS: the raw-socket client may need `SslStream` with explicit
   validation callback or a `UnityWebRequest` rewrite; cipher support on
   Valheim's Mono runtime must be proven early in stage 3.
2. Public DNS name for the volunteer endpoint is an operator decision and a
   stage-3 prerequisite (ACME needs it).
3. **Resolved — the `uid` is not a SteamID64, but `host_name` IS.** PeerInfo field 1
   is `GetUID()` = `ZDOMan.GetSessionID()` (`NETCODE-HANDSHAKE-CONTRACT.md:69`,
   `:1787`), a per-game-session long. Proven live: client SteamID
   `76561198088711642` decoded as `uid=1167002880`
   (`fieldlab/evidence/i5-handshake-live/ANALYSIS.md`).
   **An earlier revision of this entry drew the wrong conclusion from that** — it
   said the roster key does not exist on the wire and moved identity admission to
   stage 3 wholesale. It never checked `host_name`. The joining SteamID64 is right
   there: vanilla reads the host from the **socket**
   (`peer.m_socket.GetHostName()`, `ZNet.decompiled.cs:833`) and then verifies the
   Steam session ticket against that same socket identity
   (`VerifySessionTicket(ticket, zSteamSocket.GetPeerID())`, `:882`), so it is
   server-derived and Steam-authenticated rather than client-asserted, and the mod
   already forwards it (`HandshakeResponderPatches.cs:47`). The live capture is a
   bare SteamID64: `host=76561198088711642`
   (`fieldlab/evidence/i5-handshake-live/am4-server-log-decisions.txt`). So the
   roster gate is buildable **Gateway-only and live** — it is, `StrictRosterEnabled`,
   default off (§7). Caveats: valid only while crossplay is off (I6), since the
   PlayFab branch parses the same host as a `PlatformUserID` (`:891`); and the
   Gateway sees the host *before* vanilla's ticket verify, so a forged socket
   identity would pass the roster and then die at vanilla — a ghost accept (§5.5),
   which the seat lease now self-heals. The opaque `steamSessionTicket` byte[] is
   still never decoded by the mod (`HandshakeResponderPatches.cs:45`); it no longer
   needs to be for the roster, only for the stage-3 fail-closed work.
4. **Resolved in stage 2 — consumer poll/ack is the liveness signal.** Nothing
   reports a disconnect, and `_connectedUids` only ever grows
   (`ValheimHandshakeService.cs:299`), so a seat on a fixed timer is wrong in both
   directions: too short frees it mid-session, too long locks the sole volunteer
   out after a crash and lets a ghost accept (§5.5) hold the seat for the full
   lease. An earlier revision of this entry claimed the only live signal was the
   telemetry heartbeat's `peer_count`, which carries no `window_id` (only a
   per-boot `instance_id`, `TelemetryCoordinator.cs:487`) and so would have needed
   a mod cut. **That was wrong.** The authoritative consumer's own poll/ack traffic
   is already window-keyed — `/valheim/zdo-redirect/pending/{windowId}` and
   `/ack/{windowId}` (`ValheimZdoRedirectService.cs:118,125`) — and the frozen
   0.5.31 mod already sends it. `ValheimWindowActivityService` records it; a seat
   is live while its grant OR the window's consumer traffic is inside
   `SeatLeaseSeconds` (default 60). A holder who is really there refreshes it
   continuously; one who crashed or never landed stops instantly. Gateway-only, no
   mod cut. Remaining limits: liveness is window-scoped, so it cannot say *which*
   holder is alive (fine at one seat, the reason `SeatCapacity > 1` is refused), and
   a holder who runs no consumer is invisible to it.
   The seat is keyed on `host_name`, not `uid`, so a reconnecting volunteer is
   recognised and refreshes their own seat; the lease only ever gates a *different*
   player. **A previous revision claimed `_connectedUids` growth was a lockout — a
   client crashing and reconnecting without restarting Valheim keeping its `uid` and
   being rejected `ErrorAlreadyConnected` forever. That was wrong.**
   `ZDOMan.m_sessionID` is `readonly` and constructed in `ZNet.Awake()`
   (`ZNet.decompiled.cs:264`), and ZNet is destroyed on leaving a world, so every
   reconnect carries a fresh random uid. `_connectedUids` growth is therefore a
   small unbounded-memory leak whose entries never collide, not a lockout. The same
   fact is why a uid-keyed seat could not recognise its own holder returning.
5. **A Gateway accept is overturnable, and the Gateway never learns.** The mod
   hardcodes `ticketValid: true` (`HandshakeResponderPatches.cs:50`) — it never
   verifies the ticket; vanilla runs the real `VerifySessionTicket(ticket, peerID)`
   *after* the verdict (`NETCODE-HANDSHAKE-CONTRACT.md:98`, gate C). So the
   Gateway's ticket gate is dead code live, and an accept vanilla later rejects
   leaves a ghost holding the seat. Bounded today only because the password gate
   (F) runs before the duplicate gate (G), so a ghost accept costs the server
   password. This is why stage 3 must decode the SteamID64 from the session
   ticket inside the mod rather than trust a client-asserted field.
6. Retiring the shared `LUMBERJACKS_CLIENT_ACCESS_KEY` breaks any operator
   script or scraper still presenting it; inventory before stage 1 lands.
7. Strict-mode rejects surface as vanilla disconnect codes; custom reason text
   in the Valheim UI would need an extra Harmony patch (nice-to-have, not gate).
8. Consumer transport change (if `UnityWebRequest`) alters poll/ACK timing;
   re-run the priority-drain baseline before declaring stage 3 done.
   **Amended 2026-07-17 — the consumer has risk 10's bug too, and wedges quietly.**
   Risk 10 cited the consumer's `MaxResponseBytes` as proof the handshake's missing
   cap was an oversight. True, but it flattered the consumer: it bounds **size**
   (16 MiB) and not **wall clock**, so the identical trickle holds its read open
   forever. It is off the main thread (`Send` runs inside `Task.Run(Poll)` /
   `Task.Run(FlushAcks)`), so it cannot freeze the server — the failure is quieter
   and arguably nastier. `_polling` is reset in a `finally` that a wedged read never
   reaches, so the CAS at `Update` never admits another poll: **the consumer stops
   polling for good, without an error**. The tell is `poll_in_flight` stuck true.
   That matters beyond the consumer, because §5.4 makes consumer poll/ack the seat
   **liveness signal**: a degraded Gateway wedges the consumer, the traffic stops,
   and the seat lease expires out from under the volunteer it exists to protect.
   Not fixed with the handshake (`BoundedRawHttp`, comfy `b7395d3`) precisely
   because of this risk's own warning — poll/ACK is a benchmarked path. A wall-clock
   deadline is a ceiling, not a rewrite, so it should not move healthy timing at
   all; that claim is cheap to check against the priority-drain baseline and should
   be, not assumed. The extracted client already takes the deadline as a parameter,
   so adopting it is a call-site change.
9. **Decided 2026-07-17 — the manifest is the source of truth; no environment
   file.** The gate still cannot fire until the mod sends its own release identity,
   which it does not (`HandshakeResponderPatches.cs:34,39`), so this rides stage 3
   as before. The open half — env file vs release bundle — resolves to neither
   exactly, and the receipt says why. **Not an env file:** a hand-maintained
   expected-hash is a second source of truth that can disagree with the artifact
   that actually shipped, and its failure mode is the gate calling an incompatible
   release compatible — precisely what identity-pinning exists to prevent. **Not
   the bundle, either:** the bundle is unreachable at runtime — `m0-a3-release-bundle-receipt.json`
   locates it in a "local FieldLab workspace … not tracked in git; large binary
   artifacts", so no Gateway container can ever read it. What *is* reachable is the
   tracked **receipt/manifest**, which already carries `mod/ComfyNetworkSense.dll`
   sha256 under a manifest that is itself hashed. So: the Gateway's expected value
   is baked into its image at build time from the manifest, and the mod sends a
   build-time-baked `release_id` from the same manifest. Both sides derive from one
   record, so they cannot disagree except by real version skew — which is the only
   thing the gate should ever fire on. The mod should **not** hash its own DLL at
   runtime: the code doing the hashing is the DLL, so it buys no assurance for its
   cost.
   **Implemented wrong, then fixed — 2026-07-17.** The first Gateway half (`e3bc9f4`) took
   `ExpectedModReleaseId` only as a window context field set at runtime via `POST /config`:
   an operator's opinion, which is the second source of truth this entry rejected, worse for
   being per-window and un-reviewable. The decision and the code disagreed, written hours
   apart by the same hand — the exact failure risk 10 documents. Caught by *starting the cut*
   and re-reading the decision against the code, which is the only thing that catches it.
   Fixed in `9def403`: `LumberjacksExpectedModRelease` is an MSBuild property emitted as
   `AssemblyMetadata`, so the expected value is compiled in and travels with the image; the
   context **defaults** to it and the field survives only as a test override. `"dev"` = uncut
   build = no expectation = gate off. Proven by making the build flag change behaviour
   (building with `-p:LumberjacksExpectedModRelease=proof-release-xyz` fails exactly
   `UncutBuild_ReadsAsNoExpectation`) rather than by a green run, since "the baked value is
   null" passes identically when the metadata never emits.
   **Residual asymmetry:** the mod cannot use this — it sets `GenerateAssemblyInfo=false`, so
   `AssemblyMetadata` does not emit — and keeps `ReleaseId` as a const set at the cut, like
   `PluginVersion`. Both are compiled into their artifact and both are set from the manifest's
   id, so the "one record" is the manifest; but it is now **two places a cut must touch**, and
   only a cut script keeps them honest. A cut that sets one and forgets the other produces a
   Gateway that rejects the very mod it shipped with.
   **Qualified by risk 12** — the DLL hash this leans on is not currently
   reproducible across checkouts. That does not change the decision (the manifest
   is still the only reachable root), but it does mean the hash attests "the
   artifact the pipeline built", not "the artifact this commit builds", until 12
   is closed.
   **This is a compatibility gate, not an authentication gate — say so in the
   code.** A hostile volunteer can assert any `release_id`; nothing here stops that,
   and nothing needs to. Its real job is to stop the Gateway handing a *strict*
   verdict to a mod too old to enforce one: a stale mod fails **open** on a reject
   (§6), so an authority that believes it is rejecting while the mod waves players
   through is strictly worse than no gate. Absence is therefore the signal that
   matters — frozen 0.5.31 sends no release identity at all, so a missing field
   means stale by construction and needs no cooperation from the mod to detect.
   **Sequencing consequence:** absent must not mean `release_incompatible` until the
   stage-3 cut has landed everywhere, because today absence is the norm — the gate
   ships off and flips after, on the `StrictRosterEnabled` pattern (§7).
   Cheap to reverse; nothing is built on it yet.
10. **Stage-3 prerequisite: the handshake blocks the server's main thread, and
    the stall was unbounded.** The Harmony prefix runs on the dedicated server's
    main thread and `Decide` → `PostForBody` does a synchronous raw-socket
    round-trip inside it, so every join freezes the *whole server* — all players,
    not just the joiner — for the round-trip. `ReceiveTimeout` is per-`Read`, not
    for the loop, so a peer trickling one byte under each timeout held the read
    loop (and the main thread) open **forever** — over plain HTTP, with no TLS.
    That the consumer's read loop in the same repo bounds itself with
    `MaxResponseBytes` while this one did not marks it as an oversight, not a
    tradeoff. **An earlier revision of this entry called the fix "uncommitted" in
    the mod source. That was wrong, and the word did real damage** — it made the
    fix sound ephemeral and stopped anyone from looking for it. It was committed
    2026-07-16 as `cc2f95e` on branch `claude/handshake-bounded-read` in the
    `comfy` repo (the mod lives in `C:\work\comfy`, not in this one — a second
    reason it was hard to find from here). Two later commits from that branch were
    cherry-picked onto `main`; `cc2f95e` was not, so it sat stranded on the branch,
    invisible to anyone reading `main`, while this plan reported it as done.
    Recovered 2026-07-17 — cherry-picked onto `comfy` `main` as `3b9249f`, with
    `git cherry` confirming it was the only commit not already there. The bounds
    themselves are unchanged and correct: `ResponseDeadlineMs` (2000) and
    `MaxResponseBytes` (64 KiB), checked after each read, making the worst case
    finite (~`HttpTimeoutMs` connect + `ResponseDeadlineMs` read ≈ 4s).
    **Closed 2026-07-17 — the bounds are now driven by tests** (comfy `b7395d3`).
    They were untestable where they sat: `PostForBody` was a private static in a
    class bound to UnityEngine/ZNet/ZLog, and the mod had no C# test project at
    all, so loading the code meant loading Valheim. The transport moved verbatim
    into `BoundedRawHttp`, Unity-free by construction, and `ComfyNetworkSense.Tests`
    **links** that one file rather than referencing the project — compiling the real
    source into a plain net8.0 assembly that needs no Valheim assemblies, no BepInEx
    and no net48 targeting pack. Behaviour unchanged; the mod still builds net48, 0
    warnings; the numbers stay at the call site, since the caller is what knows the
    cost of blocking. Verified by mutation: disabling `ResponseDeadlineMs` fails
    exactly `ResponseDeadline_FiresBeforeSocketTimeout`, disabling `MaxResponseBytes`
    fails exactly `SizeCap_FiresOnFloodingPeer` — one bound, one test, no overlap.
    The deadline test proves *which* bound fired: its fake peer trickles a byte every
    25ms, so the 2000ms `ReceiveTimeout` is reset forever and only the 300ms deadline
    can end the read; it asserts under 1500ms, and under the size-cap mutation it
    passes at 351ms. Both fake peers stop on their own (4s trickle, 256 KiB flood) so
    a deleted bound **fails** rather than hangs — a test that wedges CI on regression
    is worse than no test. Stage 3 needs a TLS
    variant of that same client and the consumer holds a near-duplicate read loop,
    so the extraction pays for itself rather than being a detour.
    **This is a prerequisite, not a nice-to-have:** stage 3 makes this
    path fail-*closed*, and doing that on an unbounded stall is strictly worse — a
    hung or degraded Gateway would then freeze the server *and* reject everyone.
    Blocking is inherent to a Harmony prefix (it must return `bool`; it cannot
    await), so the bound is the floor, not the fix. The real options for later:
    async-then-kick (rejected players briefly enter the world), or a
    pre-authenticated signed ticket the server validates locally with zero I/O in
    the join path — the latter is the only design with no network on the join path
    at all, and stage 3 already opens the mod.

11. **CLOSED on P7 — 2026-07-17, verified on a real cold boot.** Both halves pass. The
    store is `schema_version: 2` with four enrollments for `76561198088711642`: exactly
    **one Active** (`9a3fc0e730ab`, the newest by `EnrolledUtc`, 2026-07-16T17:41:06) and
    three Revoked, each `superseded_by_migration`. `CollapseDuplicateSteamIds` did what it
    was written to do and the one-active-per-SteamID invariant holds against real data.
    The operational consequence below — the collapse assumes the player's mod holds the
    *newest* credential — **did not fire**: the live client config carries
    `lumberjacksEnrollmentId = 9a3fc0e730ab…`, which is the surviving enrollment. Nobody is
    locked out. The original entry follows.

11a. **Resolved in code, pending on P7 — the v1 migration could seed a roster that
    breaks its own invariant.** `MigrateV1` keyed enrollments by enrollment id and
    marked every v1 invite carrying an `Enrollment` as `Active`, so a v1 store with
    several redeemed invites for one SteamID migrated them all active at once —
    the exact state `RedeemLocked` refuses to create
    (`SteamEnrollmentService.cs:72`). Not theoretical: the P7 store holds ≥3
    enrollments for `76561198088711642`. Stage 1 shipped to P7 in
    `m1-clean-20260717-r1` (2026-07-17), so the migration has now executed against
    the real store; cold-start health passed, but the collapse outcome has not been
    inspected on the VM.
    Fixed before it could: `CollapseDuplicateSteamIds` keeps the newest by
    `EnrolledUtc` (ties break on id, so the survivor never depends on JSON order),
    revokes the rest with reason `superseded_by_migration`, and audits each
    collapse with its `superseded_by`. **Operational consequence to check before the
    next release cut:** the collapse assumes the player's frozen mod holds the
    *newest* credential. If that player's mod config still carries an older
    enrollment id + token pair, migration revokes the credential they actually
    present and they fail admission until an admin re-issues. Confirm which pair is
    live on that client before deploying stage 1.

12. **The mod DLL hash is not reproducible across checkouts, and the mod source is
    not byte-pinned.** Found 2026-07-17 while deciding risk 9, which leans on that
    hash. Three facts, each measured rather than reasoned:
    (a) **Line endings change the DLL hash.** Converting one source file LF→CRLF
    and rebuilding `-c Release` moves the output: `5e22cd1d…` → `c59d506b…`. The
    project sets `Deterministic`/`ContinuousIntegrationBuild`/`PathMap`, which pin
    the *path* and the compiler's nondeterminism but not the source bytes; the
    source checksum rides the debug directory into the DLL.
    (b) **A fresh clone and the working tree do not agree.** `git clone` of comfy
    materialises the mod sources CRLF; `C:\work\comfy` holds **29 of 41** of them
    as LF. `git status` reports clean the whole time, so nothing surfaces this.
    (c) **Therefore the builds differ**: fresh clone `a6f95c9a…` vs working tree
    `c59d506b…`, same commit.
    Comfy has `.gitattributes` for `fieldlab/` and `scratch/ComfyMods/` **only** —
    `git check-attr` returns nothing for the mod sources, so the exact bytes that
    produce a hash-pinned artifact are the one thing left unpinned. This is the
    same class as the M0 gotcha that hash-bound evidence must be pinned `-text`;
    it simply was not applied to the code.
    **Mechanism — corrected.** A first pass here claimed the blobs stored CRLF. That
    was wrong, off a broken measurement (`grep -c $'\r'` collapsed to an empty
    pattern and counted every line, so "40 CR lines in 40 lines" was a tautology).
    The blobs are **LF and correct**. `core.autocrlf=true` rewrites them to CRLF *on
    checkout*, and the compiler folds the source checksum into the assembly, so the
    checkout's endings land in the release hash. The corruption enters at checkout,
    not at commit — which is why `git status` never had anything to say.
    **The frozen question is no longer untested — and the answer is bad.**
    `94a3843e…` rebuilds **bit-for-bit** from its manifest commit `b32bb5e` (same SDK
    8.0.422, byte-identical Valheim/BepInEx reference assemblies, the manifest's own
    Release command) **only from an all-LF tree**. A clean checkout of that same
    commit builds `70372350…`. So the artifact deployed on P7 could not be rebuilt
    from the repository at all. The manifest's `"two clean-checkout builds produced
    the same hash"` held only because both builds shared one working tree whose files
    had never been through a smudge: they agreed with each other, not with git.
    **Fixed forward (comfy `network/mod/.gitattributes`):** `*.cs text eol=lf`
    overrides autocrlf, so a checkout now materialises the LF the blobs already hold.
    LF and not CRLF because LF is what every shipped artifact was in fact built from;
    pinning CRLF would have made the deployed DLL permanently unreproducible for a
    tidier diff. `eol=lf` rather than fieldlab's `-text`, because `-text` freezes
    whatever bytes are committed and needs discipline forever. No blob changed —
    `--renormalize` was a no-op — because only the checkout was ever wrong. This
    cannot retroactively fix `b32bb5e`, which predates the file: **rebuilding frozen
    0.5.31 needs its recipe — check out `b32bb5e`, convert every `.cs` to LF, build
    Release.** That recipe is now proven, and is the only way to verify what runs on
    P7 against source.
    **RESIDUAL, UNRESOLVED — do not call this fixed.** A first revision of this entry
    called the residual "path-dependence despite `PathMap`" and blamed something new at
    HEAD. **Both halves were wrong**, and the bisect that was supposed to confirm them
    refuted them instead. Building each mod-touching commit — `b32bb5e`, `3b9249f`,
    `b7395d3`, `ad54d93` — from two worktrees at *different* paths gives, in every case,
    the **same** hash. The build is path-independent at every commit including HEAD, and
    nothing in those three commits broke anything.
    The real axis is **clone vs worktree**, which is not the same claim and is still
    unexplained: a fresh `git clone` builds `6ba2965c…` where a worktree (and
    `C:\work\comfy` itself) builds `391c6dd8…`, from **byte-identical sources**, same
    commit, same `core.autocrlf`, same generated `obj` inputs, no SourceLink and no
    embedded git SHA. Exactly 72 bytes differ, at the PE deterministic timestamp-hash
    and the MVID/PDB checksum — the deterministic *identity* fields, so an input still
    varies. The build is deterministic in place (three clean builds of one tree agree).
    **What the hunt did turn up:** the csproj's `PathMap` never fires. The SDK injects
    its own entries ahead of it — `…\.nuget\packages\=/_1/` and `<repo root>\=/_/` — and
    csc applies the *first* matching prefix, so every source file is rewritten to
    `/_/network/mod/…` and `$(MSBuildProjectDirectory)=C:\src\ComfyNetworkSense` is dead
    code. That also explains why path-independence holds *without* the hand-written map
    doing anything, and makes `<repo root>` detection the prime suspect for the clone/
    worktree split. Next probe: dump SourceRoot on both sides properly (an attempt at
    this returned a bogus "identical" off a broken `sed` — distrust it), or take a binlog
    of each and diff the Csc task inputs.
    Consequence unchanged: the LF corruption is closed, but **rebuild-to-verify is still
    not established**, and risk 9's hash still attests "what the pipeline built" rather
    than "what this commit builds".
    **Note:** `ZdoAuthoritativeConsumerRunner.cs` was LF at session start, went CRLF
    when `git checkout` re-smudged it mid-investigation, and is LF again under the pin.
    **SOLVED 2026-07-18 — the varying input was git HEAD itself.** The prescribed next probe
    (Csc input diff, then PDB diff) found it: the .NET 8 SDK's implicit source-control tasks
    embed the HEAD sha in the portable PDB when the origin is a recognized host (this repo's
    github URL); the PDB checksum rides in the DLL's debug directory — those are the 72
    identity bytes. A local-path clone gets no embedding at all (origin unrecognized, PDB 144
    bytes smaller): that is the whole clone-vs-worktree split, and worktrees matched because
    they share the main repo's origin and HEAD. Caught live: a script-only comfy commit
    (`877ff11`) moved the tree's mod hash `b6224cd9` → `b44349db` with zero source changes.
    Proven both directions: `-p:EnableSourceControlManagerQueries=false` makes clone and tree
    converge on one hash (`9d108121`), which equals what the clone built with queries ON —
    the clone was already building the nothing-embedded flavor.
    **Still open, but now a decision rather than a mystery:** the cut builds BEFORE the
    release commit exists, so a shipped DLL embeds the PARENT sha and no checkout of the
    release commit can rebuild it. Either pin the queries off in the mod csproj (hash attests
    source alone, loses embedded provenance) or reorder the cut commit-first-build-second
    (keeps provenance; rebuild then also needs the same origin URL). Until one is chosen,
    rebuild-to-verify remains unestablished — but its blocker is named, reproduced, and
    reversible. Recorded in comfy `554488d` (the cut script's parting text now states this).

13. **Fixed — the release cut's artifact comparison was broken and had never executed.** Script
    `C:\work\comfy\infra\gcp\p7\scripts\New-ReleaseCut.ps1` (added in comfy `3e6f6304`) implements
    risk 9's fix: type the release id once, build both sides, then read the id back out of BOTH
    compiled DLLs and refuse if they disagree. The comparison was believed to be an active
    safeguard; in reality it had never executed, because it sits behind `-WhatIf` and only runs
    during a real cut — the one moment it matters.
    **Mechanism — it could not have worked.** `Get-AssemblyMetadataValue` reached the reader
    through `Add-Type -AssemblyName System.Reflection.Metadata`. .NET Framework has no GAC entry
    for that assembly, and this box has only Windows PowerShell 5.1 (no pwsh 7). Every real cut
    would have thrown a type-not-found *at the check* — after the mod source edit and both builds
    (including the `sdk:9.0` container gateway build) had already run, leaving a half-applied
    state reversible with `git checkout`. `-ErrorAction SilentlyContinue` on that `Add-Type`
    suppressed the only error that explained the cause.
    **Fixed (comfy `9fa4858`):** the `netstandard2.0` builds of `System.Reflection.Metadata` and
    `System.Collections.Immutable` are vendored into `infra/gcp/p7/scripts/lib/` and loaded by
    explicit path in dependency order, failing loudly if absent. Vendored, not resolved from the
    nuget cache, because a cache clear would silently disarm the check; and `.gitignore`'s blanket
    `*.dll` would have silently skipped them on `git add`, so the fix would have worked on this box
    and been absent from every clone — overridden explicitly.
    **Verified by lifting the real functions** out of the real script and running them against the
    artifacts on disk: the mod DLL carries `LumberjacksModReleaseId='dev'`, the gateway DLL carries
    `LumberjacksExpectedModRelease='proof-release-xyz'`, and the verdict logic refuses on all three
    comparisons. First time the check has ever returned an answer.
    **Checked and cleared — not defects:** (1) the comparison logic itself is sound — a missing
    metadata key returns null, and null never equals a regex-enforced non-empty `ReleaseId`, so
    absence fails closed rather than falsely passing. (2) An audit suggested `$modDll` pointed at a
    stale path missing a `net48` folder; refuted — the mod csproj sets
    `AppendTargetFrameworkToOutputPath=false`, so `bin\Release\ComfyNetworkSense.dll` is correct.
    **Residual resolved 2026-07-18 — the pass path is now known.** A rehearsal cut
    (`m1-rehearsal-20260718-r1`; full non-WhatIf run: const edit, both builds — mod native
    net48, gateway through the sdk:9.0 container — artifact readback, then reverted) returned
    the first pass verdict the check has ever produced: both DLLs agreed. The check is proven
    to refuse (`9fa4858`) and to pass. The rehearsal also caught a defect: the mod csproj's
    `CopyAssembly` convenience target copies every build into the live Steam client's plugins
    folder, so the release-id DLL landed on the OMEN client BEFORE the identity check ran — a
    failing cut would already have shipped locally. Fixed in comfy `877ff11`: the cut's mod
    build points `PluginOutputPath` at a nonexistent dir (verified both ways: suppressed build
    leaves the plugins mtime untouched, plain build still copies). Risk 12's rebuild-to-verify
    gap: root cause found the same day — see risk 12.

## 6. Wire constraints (frozen 0.5.31)

The mod regex-matches the verdict body rather than deserializing it
(`HandshakeResponderRunner.cs:181,194,195`). Extra response fields are therefore
free; the shape of a *reject* is not.

- **A reject needs all three, or it fails open.** HTTP 2xx (`:254`), a body that
  does **not** match `"accept"\s*:\s*true`, and a literal `"error_code":<digits>`
  (`:194,205-208`). Miss any and the mod passes the player through as
  `unparseable_verdict` (`:196-204`). Non-2xx, timeout, or socket fault fails open
  as `endpoint_error` (`:171-177,236-238`) — so never route handshake rejects
  through auth middleware that can answer 401/403.
- **Every reject maps to a native `ValheimConnectionStatus` int.** No int, no
  reject: `capacity_reserved` → ErrorFull (9), `release_incompatible` →
  ErrorVersion (3), identity rejects → ErrorBanned (8).
- **Never echo a client-controlled string into the response body.** Accept-matching
  is a substring regex, so a `player_name` of `","accept":true,"x":"` forces an
  accept — unauthenticated. `player_name`, `host_name`, and `version` all come from
  the client's packet. Not a live bug today (`failed_check` values are hardcoded
  literals, `ValheimHandshakeService.cs:370-371`); a hard constraint on stage 2.
- **Reason codes are for operators, not players.** Only the integer code reaches
  the client (`HandshakeResponderPatches.cs:48`); `failed_check` lands in the
  server log only. Granular reasons ride `failed_check` plus new additive fields.
- **`window_id` stability is a config accident.** It comes from mod config
  (`HandshakeResponderRunner.cs:64-67`); unset it regenerates per boot as
  `i5-<timestamp>`, set it is stable across restarts. Do not assume either.

## 7. Stage 2 build status — 2026-07-16

Built and committed (`cd78296`); **deployed** to P7 on 2026-07-17 in
`m1-clean-20260717-r1`, with `StrictRosterEnabled` still off (§7 decision).
Full solution green: 458 tests, 0 failures (Gateway 92 = 81 baseline + 11 new).

**Shipped live.** The seat gate (`capacity_reserved`), as check **H** — after all six
native checks, so a client vanilla would reject still gets vanilla's code and label.
Seat state is in-memory in `Window` under the existing lock, expiring passively.
`ValheimWindowActivityService` supplies liveness from consumer poll/ack (§5.4).
Rejection records and per-code counters already existed and needed no work.

**Decisions taken while Derek slept** — each is cheap to reverse, none are load-bearing
on anything deployed:

- *Reason codes ride `failed_check`; no new response field.* The plan allowed additive
  fields (§6) and the mod's regex parser makes them safe, but `failed_check` already
  carries the label the mod logs to the server log, which is exactly where an operator
  wants it. A parallel `reason_code` would have duplicated it for no reader. Native
  gates keep their six contract labels; only Lumberjacks gates use plan reason codes.
- *`SeatCapacity` defaults to 1, so an unconfigured window enforces one seat.* The
  window that materialises on first contact is the live path (that is how Derek's
  2026-07-16 join was gated), so a default of 0 would have shipped the gate switched
  off. One seat is the platform's actual intent, not a placeholder.
- *`SeatLeaseSeconds` defaults to 60.* See the field's own comment for the reasoning.
- *Time is injected* (`Func<DateTime>`) rather than read from `DateTime.UtcNow`, because
  lease expiry is untestable otherwise and this codebase has no clock abstraction.
- *Verified by mutation, not by green.* Stubbing the gate fails exactly the 3 tests
  asserting a reject; forcing liveness never to expire fails exactly the 4 asserting
  expiry. 6 of 11 are mutation-sensitive; the other 5 are regression guards against
  over-rejecting, and a mutation that would prove them is still owed.

**The roster gate, live behind a flag.** Built after the §5.3 correction: `host_name` is
the socket's Steam-authenticated SteamID64, so the roster is answerable Gateway-only and
did not need the mod cut. `StrictRosterEnabled` **defaults off** — a frozen mod treats a
reject as a reject (only endpoint faults fail open), so a roster miss locks the sole
volunteer out of their own server, and that is not a switch to throw unattended. It runs
before the seat gate, so a stranger cannot consume a seat on the way to being rejected.
A strict window with no roster source wired is refused at `Configure` rather than
admitting everyone. `SteamEnrollmentService.CheckSteamId` separates `not_enrolled` from
`enrollment_revoked` because they are different operator stories.

**Cut from M1 — 2026-07-17.** `lease_stale`: nothing issues a readiness lease. No endpoint,
no record, no field. M1 named it as a deliverable but never said who mints one, what it
attests, or how long it lives — because M1 has no consumer for one. M4a does: it owns exact
per-peer readiness and reconnect/takeover rules, requires an exact per-peer readiness lease
in its own work, and tests lease takeover at its exit. M1 was holding a contract on M4a's
behalf, so the lease leaves M1's exit gate, and M4a's inputs stop listing it among the
contracts M1 delivers. Building it here would have hard-coded an identity model the mod cut
then inherits, to satisfy a gate whose only reader specifies it differently and per peer.
An earlier revision of this decision moved the lease to M5; the dependency graph refutes
that — M4a depends on M1 alone, so an M5-owned lease would make M4a wait on a milestone it
does not depend on.

**Live verification on P7 — 2026-07-17, cold boot.** Three things that could only be checked
with the VM up, all passing:

- **The durable pin holds.** This is the one a green drill can silently break: the promotion's
  pin was hand-fixed and never re-verified, and a stale `LUMBERJACKS_GATEWAY_IMAGE` reverts the
  gateway on the next reboot while the *running* container looks right. It doesn't:
  `/etc/comfy-p7/environment` pins `lumberjacks-gateway:m1-clean-20260717-r1`, the tag resolves
  to `3576d8e03fb4`, and the container that came up from cold runs
  `sha256:3576d8e03fb49b6a…` — the promoted digest. `/health` 200. The M0 rollback image
  (`141bd9e5`) is still on disk. Chain verified end to end through an actual reboot.
- **Risk 11 closed** (above): the migration collapsed correctly and the live client holds the
  surviving credential.
- **The roster answers correctly.** Verified against the *real* store on throwaway windows, so
  the live window was never touched: enrolled `76561198088711642` → accept; stranger
  `76561190000000001` → reject, code 8, `not_enrolled`; and — the part that makes it
  conclusive — the same stranger on a **non-strict** window → accept, proving the rejection
  comes from `StrictRosterEnabled` and not from some other gate.
  A first attempt at this was junk and is worth recording: reusing one `uid` across both probes
  meant the stranger hit gate G (duplicate) before the roster ever ran, and it *looked* like a
  reject. The ladder was right; the probe was wrong. Distinct uids fixed it.
  **FLIPPED, and then verified by a real join.** `StrictRosterEnabled` is **on** for the live
  `p7-primary-v1` window as of 2026-07-17. The synthetic probes above were not the last word:
  at `14:02:23.453Z`, fourteen minutes after the flip, Derek joined for real and the window
  recorded a third exchange — **accept**. Server log:
  `Got connection SteamID 76561198088711642` → `[handshake] ACCEPT (Lumberjacks-decided)
  window=p7-primary-v1 uid=1110941871 player=Durracktu host=76561198088711642` → `AddPeer`.
  Window counters `accepted=2 rejected=1 by_code={8:1}`: two probes and the join, on a window
  that was already strict.
  So the precondition — "verify the roster answers correctly against real joins, then flip" —
  is now met **literally**, not in substance. Note the order was inverted: flip first, real
  join second. That was the riskier sequence and worked only because the store said Active and
  the probes agreed first; it is not the order to repeat.
  **What this proves beyond the gate itself:** the join was decided by the **frozen 0.5.31 mod**
  (`94a3843e`), armed at `http://gateway:4000`, with no mod cut. Vanilla read
  `host=76561198088711642` off the socket and the Gateway answered from the real enrollment
  store. §5.3's correction is now evidenced live rather than argued from a capture — the earlier
  revision that deferred the roster to stage 3 on the belief that no Steam identity reached the
  Gateway was wrong, and this is the proof.
  **Configure replaces, it does not merge** (`_context = context`), and it clears connected uids
  and drops seats. Safe here only because the window was fresh from boot and every record default
  matched the live values; reconfiguring a live window mid-session would drop a player's seat.
  The flag is in-memory, so a gateway restart reverts it to off.

**Answered by Derek — 2026-07-17.** (1) Readiness lease: cut from M1, above. (2)
`StrictRosterEnabled` stays **off** through the cut: deploy it disabled, verify the roster
answers correctly against real joins, then flip with a way back — rather than stacking the
first switch that can lock Derek out on top of a migration that had never run against real
data, where a lockout and a migration defect would arrive together and be hard to tell
apart. The deploy half is now done: `m1-clean-20260717-r1` shipped the gate to P7 with the
flag off, so what remains is the live roster verification and then the flip. (3)
`SeatCapacity` default of 1 is **confirmed**: the window that materialises on first contact
is the live path — it is how the 2026-07-16 join was gated — so a default of 0 would ship
the capacity gate switched off.
