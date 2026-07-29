# MCP Mod Channel

A localhost MCP server that talks to the running game mod: apply a config
profile, run a bounded lab test, read back what actually happened.

## What it is

A local MCP gateway, intentionally separate from the fleet-wide Hearth
gateway — its own auth header, port, ledger, and tool registry, scoped to
Valheim mod development. It listens at `http://127.0.0.1:8720/mcp`
(header `X-Comfy-Key: comfy-dev-local` for dev use; a separate
`valheim-mod-local` key is reserved for a future in-mod client) and also
exposes a few plain dev HTTP routes (`/healthz`, `/valheim/report`,
`/valheim/next-test`, `/valheim/config-suggestion`,
`POST /valheim/apply-profile`).

Its tools (source of truth: `network/mcp/contracts/commands.json`) read
NetworkSense telemetry and BepInEx logs, summarize recent sessions,
compare host/client bundles for multiplayer tests, suggest (but don't
apply) config changes, apply a whitelisted config profile when asked, run
one bounded named motion command against a disposable lab client, and get
a local-Ollama explanation of a report. It's genuinely wired to the live
mod, not a mockup — `valheim_apply_config_profile` really edits the mod's
`.cfg`, and `valheim_lab_motion_test` really drives a disposable client
through an atomic mailbox file.

## What it is NOT

Not a remote-admin surface, and never exposed off-host — every design rule
in the README says so: localhost only, dev-only, JSONL stays the source of
truth, calls are command- or event-triggered and never per-frame, and
model output is treated as text/suggestions, never executed automatically.

Not a console or shell bridge. The one mutation seam that touches gameplay
— the lab motion tools — only accepts named patterns and bounded
durations, requires `COMFY_AUTONOMOUS_STATE` to be set, and cannot run
arbitrary commands.

## Status

It runs, and it really does drive the live mod — from `127.0.0.1` only,
with a dev key, on purpose.

## Run it in about 20 minutes

1. Get a Python environment with `mcp==1.28.1` installed. Set
   `COMFY_GATEWAY_PYTHON` to that interpreter's path if you don't want to
   rely on the fallback chain below.
2. From the repo root: `.\network\mcp\etc\start-comfy-gateway.cmd`. It
   picks its interpreter in this order: `%COMFY_GATEWAY_PYTHON%` (if set)
   → Hearth's OMEN venv, if that path happens to exist on your machine →
   plain `python` on `PATH`. Whichever one it picks still needs
   `mcp==1.28.1` installed — the env var only changes which interpreter
   runs, not the dependency.

   Or run it directly:
   `$env:PYTHONPATH = "C:\work\baseline\network\mcp"; python -m comfy_gateway.kernel.gateway`
3. From any MCP client, point it at `http://127.0.0.1:8720/mcp` with
   header `X-Comfy-Key: comfy-dev-local`, and list its tools.

## What you'll see

A running local server on port 8720. `curl http://127.0.0.1:8720/healthz`
should answer. From an MCP client you'll see roughly 16 tools registered —
`comfy_gateway_status`, `local_generate`, and the `valheim_*` family
(`valheim_networksense_report`, `valheim_list_sessions`,
`valheim_session_bundle`, `valheim_apply_config_profile`,
`valheim_lab_motion_test`, and more). If the mod isn't running locally yet,
the gateway still starts; the telemetry-reading tools will just have
nothing to report until it is.

## What's rough

- Reaching `X-Comfy-Key` currently means using the shared dev value
  (`comfy-dev-local`) — there's no per-caller key issuance yet, so treat it
  as "trusted local dev," not "authenticated."
- The `valheim-mod-local` key is reserved in the docs for a future in-mod
  client that doesn't exist yet.
- `network/mcp/tests/test_gateway_basics.py` covers the ledger, auth
  resolution, the NetworkSense report path, whitelisted config-profile
  application, and the netcode-gate tool registration (6 tests total) — but
  that's the whole automated test surface for a fairly large tool list.

## First tasks

- **MC-1 — Run the gateway, list its tools from any MCP client, and post
  the list.** Done when: the tool list from your own client is posted in
  the #workbench forum — this tool's own thread has not opened yet — along
  with which client you used and anything in the setup that wasn't obvious.

## Where to talk about it

The #workbench forum, for now. This tool's own thread has not opened yet;
when it does, the workbench card's Discuss link will point at it.

## License & privacy

BSL 1.1 public-source posture — this code is in `network/mcp/` in this
repo, covered by the root `LICENSE` / `LICENSING.md`.

Privacy/safety: this gateway is bound to `127.0.0.1` on purpose — its
entire safety story is that only your own machine can reach it. Do not
expose port 8720 beyond localhost, and do not wire its output into
anything that executes automatically; every design rule above exists to
keep a model's output advisory, not authoritative, over a live game
process.
