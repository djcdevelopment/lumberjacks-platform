# Harmony patch policy — ComfyNetworkSense

Status: adopted 2026-07-24. Codifies what the mod already practices so transpiler use stays
deliberate. Scope: `network/mod/ComfyNetworkSense` (BepInEx 5.4.2202 pack → HarmonyX 2.10.x,
Valheim 0.221.12, Unity Mono, net48).

## Default shape

- **Attribute prefix/postfix, applied unconditionally in `Awake`** via
  `Harmony.CreateAndPatchAll`, feature-gated *at runtime inside the body* (the runner no-ops
  unless armed). Patching in `Awake` runs before `ZDOMan`/`Game` exist and before any hot
  method is first JIT-compiled, so there is no inlining race for our targets.
- Manual `Apply()` patches are allowed for optional features but must also run in `Awake`,
  each in its own soft-fail try/catch (see `PanelInputPatches`, `GameplayEventPatches`).

## Transpilers

- **Only for surgical call-site swaps** where a prefix/postfix cannot express the change —
  the exemplar is `ZdoSendCadenceOverridePatches.UpdateTranspiler`, which retargets one
  `call` operand inside `ZDOMan.Update` and leaves every other instruction untouched.
- A transpiler must **degrade to a no-op**: if the `CodeMatch` misses (game update moved the
  call site), return the original instructions and report unavailability via telemetry
  (`PatchInstalled = false`). Never emit stack-shape changes.
- Never use a transpiler for logic a flag-gated prefix can do. Volunteers run
  `lumberjacks-primary` with native ZDO sync fully suppressed — there is **no vanilla
  fallback**; malformed IL is a client-bricking risk, and the mod-zip channel makes a
  synchronized redeploy expensive.
- Mono note: our HarmonyX carries the `Leave`/`Nop` fix for the Unity Mono try-catch
  emitter bug; still, avoid transpiling methods with exception handlers unless the rewrite
  actually changes instructions.

## Inlining ladder (from NETCODE-MAP)

The receive/handshake/routed-RPC layer is delegate-registered (`ZRpc.Register`) and cannot
be inlined. Residual risk is the private send-side helpers (`SendZDOs`, `CreateSyncList`,
`RouteRPC`). If one ever inlines under a game update, escalate in order:

1. postfix a caller-level seam (`CreateSyncList`),
2. transpiler on the caller's call site,
3. hook the caller one frame up.

## Stacking and ordering

- When two patches share a method and one can skip the original or mutate state the other
  reads, **ordering is load-bearing**: observers take `Priority.Last` (or the mutator takes
  `Priority.High`), as with `ZdoRedirectPatches` (High) before `NetcodeProbePatches`
  (Normal) on `CreateSyncList`. Record the intended order in a comment at the patch site
  (retro lesson L-2026-07-10-6).

## Hot-path cost

- Detour overhead is **measured, not assumed**: hot patch bodies are wrapped in
  `NetworkSensePerfProbe.MeasurePatchLoad(...)`, which accumulates per-call cost and emits
  per-interval rollups to `perf-patchload.jsonl` when `perfPatchLoadRollupEnabled=true`
  (default OFF — lab-only; volunteer telemetry stays lean).
- Before any IL micro-optimization of a patch, produce an A/B `p95_frame_time_ms` +
  patch-load comparison on a lab client. The known perf ceiling is send volume
  (`observers × changed-area-ZDOs`, ADR 0011/0013), not detour frames.

## Invariants that outrank any patch

- Suppress/ack/emit: every ZDO removed from native send must be acked into `peer.m_zdos`
  (per observer, once fan-out lands) — violation is a duplicate storm (ADR 0011/0013).
- Any patch that writes ZDO state must prove clean quit-and-reload before shipping.
- `UnpatchSelf()` in `OnDestroy` stays mandatory.
