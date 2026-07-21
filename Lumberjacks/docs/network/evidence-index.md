# Network Evidence Index

This ledger joins design intent, implementation, verification, and client use. Status
describes the current repository, not an aspirational roadmap.

| Capability | Intent / research | Implementation | Verification | Consumer / status |
|---|---|---|---|---|
| Server authority | ADRs 0001, 0005, 0014 | `InputQueue`, `SimulationStep`, `TickLoop` | Input queue and simulation tests | Nature 2.0 sends intent; implemented |
| Delivery semantics | ADRs 0003, 0008 | `DeliveryLane`, `MessageClassification` | Message classification tests | Gateway routing; implemented |
| Binary framing | ADR 0012 | `BinaryEnvelope`, `MessageTypeId` | Envelope and mapping tests | Gateway and Nature 2.0; implemented |
| Compact input | ADRs 0012, 0014 | `PayloadSerializers.WritePlayerInput` | Payload serializer tests | Five-byte payload; implemented |
| Compact vectors | ADR 0012 | `CompactVec3` | Compact vector tests | Entity updates; implemented |
| Compact entity update | ADR 0012 | `WriteEntityUpdate` / `ReadEntityUpdate` | Payload serializer tests | Player updates implemented; stale 19-byte code comment |
| Spatial indexing | ADR 0015 | `SpatialGrid` | Spatial grid tests | Player visibility; implemented |
| Interest tiers | ADR 0015 | `InterestManager` | Interest manager tests | Player updates only; implemented with limited entity coverage |
| UDP channel | ADR 0013 | `UdpTransport`, session token binding | UDP packet tests, load script | Node load client; implemented |
| WebSocket fallback | ADR 0013 | `TickBroadcaster` | Local/Azure load report | Nature 2.0 uses this path; implemented; its input lane bit currently drifts from classification |
| State hashing | ADR 0014 | `StateHasher` | State hasher tests | Carried in entity update; implemented |
| Client reconciliation | ADRs 0014, 0017 | Sequence and hash support | Server-side tests | Full Godot replay/correction not implemented |
| Terrain simulation | Worldgen plans | `TerrainSim`, `ParameterSweep`, `WorldGenLab` | 500-run sweep and interactive presets | Lab validated; server promotion incomplete |
| Tree-felling simulation | Forestry sources, tree plan, ADR 0019 | `TreeFellingSim`, `TreeFellingLab` | Presets and interactive lab | Lab validated |
| Compact tree projection | ADR 0019, tree plan | `CompactTreeState` in lab code | 24-byte display/budget check | Not in shared serializer or broadcaster |
| Natural-resource transport | Nature 2.0 plans | JSON resource update in `TickBroadcaster` | Vertical client work | Binary/AoI path incomplete |
| Valheim priority delivery | July gateway work | priority manifest planner and endpoints | Contract tests | Gateway extension; implemented in repository |
| Valheim redirect/injection/handshake | July gateway work | `src/Game.Gateway/Valheim/` | Gateway tests, copied-WAL replay, and P7 single-client window | Active infrastructure extension; Valheim relevance remains native-side |
| Valheim authoritative priority ZDO consumer | Server-owned persistent truth; reliable, importance-ordered mutation delivery | `ValheimZdoRedirectService`, `ValheimZdoConsumerTelemetryService`, shared ComfyNetworkSense priority classifier, background poller, Unity `RPC_ZDOData` client path | 2026-07-16 P7 clean gate: 83,220/83,220 acknowledged, 0 pending, 100% priority tagged, 0 native-only/rejects/duplicates/retries/transport failures; 46 Gateway tests | Proven for one enrolled client and the observed ZDO window; recipient-scoped multi-client queues and broader authority remain unproven |

## Primary contemporaneous records

- [Network refactor plan](../../implementation_plan.md)
- [Simulation audit](../simulation-audit-2026-03-26.md)
- [Simulation retrospective](../simulation-retrospective-2026-03-26.md)
- [Thesis compliance audit](../thesis-compliance-audit-2026-03-27.md)
- [Dual-channel load results](../load-test-dual-channel-results.md)
- [Architecture decisions](../adrs/)
- [Tree-felling lab plan](../plan-tree-felling-lab.md)
- [World-generation lab plan](../plan-worldgen-lab.md)
- [Valheim + Lumberjacks P7 overview](valheim-lumberjacks-p7-overview.md)
- Versioned P7 victory evidence: Comfy repository `fieldlab/evidence/p7-primary-v1-authoritative-priority-zdo-20260716-v0531.md` (permalink resolvable once staged revision 433f1cc is pushed).
- P7 victory report (sanitized publication copy): Comfy repository `fieldlab/evidence/p7-gold-run-20260716-011112-authoritative-priority-cutover/report.md` (permalink resolvable once staged revision 433f1cc is pushed; raw immutable packet remains local under `fieldlab/runs/`).

## Evidence rules for future updates

- Link claims to a file, test, result, or decision record.
- Label dependency-based history as reconstructed.
- Separate a lab projection from a shared serializer.
- Separate shared serializer support from gateway transport.
- Separate gateway transport from a client that actually uses it.
- Date benchmark results and retain raw output when possible.
- For Valheim, record native relevance selection separately from Lumberjacks `InterestManager` AoI; do not merge those claims into one coverage number.
