# I Built a Multiplayer Game Backend in 32 Hours With an AI Pair Programmer

*And deployed it to Azure. Here's what actually happened.*

---

Last week I had a design document, 11 architectural decision records, and a TypeScript scaffold that didn't compile. Two days later I had a multiplayer game backend running on Azure, proven with 10 concurrent players, 157 passing tests, and a deployment runbook.

I used Claude as a pair programmer for the entire build. Not "generate some boilerplate" usage — full architectural partnership across a 32-hour sprint. Here's what I learned about what AI pair programming actually looks like on a real project.

## The Project

I'm building a community survival game platform — think Valheim, but where the server owns everything and the game client is a thin rendering shell. The architecture is designed for community operators to run their own worlds, with server-authoritative gameplay that prevents cheating by design.

The key technical bet: the server decides where every player is, what every structure looks like, and when every challenge completes. The client just renders what it's told and sends input. This is the opposite of how most game engines work, and it means the backend is the actual product.

## What Got Built

**Day 1** was foundation. We replaced the TypeScript scaffolds with a .NET 9 solution — 8 projects, 100+ domain classes. By the end of the day, a player could connect via WebSocket, join a region, place a structure, trigger a guild challenge, and see progression evaluated. The vertical slice worked end-to-end.

Then we got ambitious. The same evening, we implemented all five phases of a network optimization plan I'd been designing:

- **Binary serialization** — Custom bit-packing that compressed entity updates from ~200 bytes to 33 bytes (84% reduction) and player input from 120 bytes to 5 bytes (96% reduction)
- **Input-driven simulation** — Deterministic server physics with input queuing, so the server computes all movement from client direction/speed input
- **Spatial interest management** — A grid-based spatial hash for O(1) radius queries, with area-of-interest filtering that drops bandwidth by 90%+ for distant entities
- **Dual-channel transport** — UDP alongside WebSocket, with automatic fallback. Unreliable messages (position updates) go over UDP; reliable messages (structure placement) stay on TCP

**Day 2** was deployment and validation. We deployed four services to Azure Container Apps with a PostgreSQL backend, ran 10-player multiplayer tests against the live deployment, built proxy endpoints for remote testing, and wrote a comprehensive deployment runbook.

## What Surprised Me

**AI is remarkably good at maintaining architectural coherence across a large codebase.** When we moved Postgres to a different port, it meant updating 13 files — connection strings, Docker configs, fallback values in every service's Program.cs. Claude tracked all of them. When we consolidated two services into one, it updated proxies, test scripts, startup scripts, Docker Compose, and documentation in a single pass. I've done these kinds of cross-cutting changes manually, and I always miss one.

**The debugging was genuinely collaborative.** When the admin dashboard showed 0 structures despite tests placing 10, Claude traced the proxy chain: OperatorApi was routing to the standalone Simulation service (which had an empty world state) instead of the Gateway (which runs the simulation in-process). That's a multi-hop architectural reasoning problem, not a syntax error. When Docker builds silently served stale cached layers, we figured out together that `--no-cache` and unique image tags were both needed.

**Speed changes what you attempt.** I wouldn't have tried to implement binary serialization, UDP transport, spatial partitioning, AND deploy to Azure in the same sprint if I were working alone. The AI pair programmer doesn't just write code faster — it changes the calculus of what's worth attempting in a given time window. Some of the "too ambitious" ideas became "let's just do it."

**Documentation kept pace with development for the first time.** Usually docs are the thing I plan to write "after." In this sprint, ADRs, deployment runbooks, and architectural audits were produced inline with the code. Claude wrote first drafts; I edited for accuracy and tone. Having comprehensive docs 32 hours into a project is unusual, and it's already paying off — I can hand someone the deployment runbook and they can deploy without me.

## What Didn't Work

**Docker layer caching burned us.** We built a new image, pushed it, updated the Azure container... and the old code was still running. `latest` tags and Docker's build cache conspired to serve stale binaries. We lost about 30 minutes before figuring out that `--no-cache` and unique tags were both necessary.

**AI doesn't know your local environment.** Claude assumed bash when I use PowerShell. It assumed `az` was on PATH when it wasn't. It suggested commands that work on Linux but fail on Windows. These are small friction points, but they add up. The fix is being explicit about your environment upfront and correcting early — AI learns fast from corrections.

**First-pass solutions sometimes need architectural rethinking, not just fixes.** When test scripts failed against Azure because they hit internal-only services directly, the first instinct was to make those services external. The right answer was to route through the OperatorApi proxy layer. AI will optimize for making the current error go away; you need to steer toward the architecturally correct solution.

## The Numbers

| Metric | Value |
|--------|-------|
| Calendar time | 32 hours |
| Commits | 13 |
| Files changed | 180 |
| Lines of code (net) | +9,879 |
| .NET projects | 8 |
| Tests passing | 157 |
| E2E test scripts | 6 |
| Azure services deployed | 4 |
| Monthly Azure cost | ~$25 |
| Bandwidth per client | <3.6 KB/s |

## What I Actually Think About AI Pair Programming

It's not magic and it's not a replacement for knowing what you're building. Every major decision — the server-authoritative architecture, the dual-lane transport design, the spatial interest management strategy — came from domain knowledge I've built over years of running Valheim communities and thinking about multiplayer game infrastructure. AI didn't have those insights.

What AI did was execute on those decisions at a pace I couldn't match alone. It turned architectural diagrams into working code, maintained consistency across dozens of files, wrote test suites that actually caught bugs, and produced documentation that I'd normally skip. It turned a month of evenings-and-weekends work into a focused sprint.

The project isn't done — next up is the Godot game client. But the backend is deployed, tested, and documented. A friend could download a test script, point it at my Azure endpoint, and play alongside me right now. Two days ago that was a planning document.

If you're building something and thinking "that's too much for one person" — you might be wrong about how much one person can do now.

---

*Building a community survival game platform. Previously operated Valheim communities. Interested in server-authoritative multiplayer, game infrastructure, and what AI tooling means for solo founders.*
