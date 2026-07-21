# ADR 0006: Godot as Game Client Engine

**Status:** Accepted
**Date:** 2026-03-26
**Supersedes:** None

## Context

The platform needs a game engine that serves as a thin rendering shell (per ADR 0001). The backend owns all authoritative truth — the client handles rendering, input capture, client-side prediction, and interpolation. Two viable candidates were evaluated: Unity and Godot.

Key requirements:
- First-class modding and extension support as a core platform value
- Open tooling that community operators can inspect and extend
- No built-in multiplayer assumptions that conflict with our custom protocol (ADR 0003)
- Viable for 3D survival gameplay at the visual quality level of Valheim

## Decision

**Godot 4.x** is the game client engine.

## Rationale

**Modding as a first-order citizen.** Godot is MIT-licensed and open source. Scene files (`.tscn`) and resources (`.tres`) are human-readable text formats that version cleanly in git and are inspectable by modders without special tooling. GDExtension allows native C/C++/Rust plugins without engine forks. GDScript is approachable for community content creators. Unity's modding story requires third-party frameworks (BepInEx, Harmony) that patch compiled assemblies — fragile across Unity version updates and opaque to operators.

**No wasted abstraction.** Neither engine's built-in multiplayer helps us. Unity's Netcode for GameObjects and Godot's MultiplayerAPI both assume the engine owns authoritative state. Since our backend owns truth and the client is a rendering shell with a custom WebSocket/datagram protocol, built-in netcode is irrelevant. This neutralizes Unity's perceived networking advantage.

**Operator and community alignment.** An open-source engine matches the community-operated philosophy. Operators can build tools, inspect client behavior, and contribute fixes upstream. No license fees, no runtime royalties, no seat restrictions.

**Trade-offs accepted:**
- Godot's 3D renderer is less mature than Unity's — acceptable for the Valheim-inspired art style (stylized, not photorealistic)
- Smaller asset store ecosystem — mitigated by the modding community we intend to cultivate
- Fewer AAA-proven references — acceptable given our server-authoritative architecture where the client is deliberately thin

## Consequences

- Client code will use GDScript and/or C# (via Godot's .NET integration) for game logic
- Modding SDK (future) will target GDExtension and stable GDScript APIs
- Art pipeline targets Godot's rendering capabilities and import formats
- No dependency on proprietary engine licensing terms
