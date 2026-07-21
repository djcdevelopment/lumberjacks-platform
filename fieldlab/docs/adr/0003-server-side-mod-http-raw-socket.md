# ADR 0003 — Server-side mod HTTP over a raw TcpClient, not WebRequest.Create

- **Status:** Accepted (2026-07-10)
- **Rung:** I3 / P4 (outbound redirect); binds all future **server-side** mod HTTP (I4/P5 injection receipts, I5/P6 handshake callbacks)

## Context

The `ZdoRedirectRunner` POST — and every other HTTP call site in `ComfyNetworkSense` (telemetry,
priority-mirror, apply-profile) — used `WebRequest.Create("http://…")` / `HttpWebRequest`. That works
in the **client** Unity Mono runtime, so it was never suspect. It does **not** work in Valheim's
**dedicated-server** stripped Mono runtime: `WebRequest.Create` throws
`NotSupportedException("The URI prefix is not recognized.")` because the runtime's WebRequest prefix
table (normally populated from `machine.config`'s `<webRequestModules>`) is empty there. Window i3-w3
suppressed 88 ZDOs correctly (`ack_failures=0`) but **posted 0 and dropped all 88** — every POST threw
before a byte left the box. A container `curl`/GET `/health` succeeds even in this state, so a
health-only pre-flight does not catch it; only a real POST from the server runtime does.

## Decision

**Server-side mod HTTP goes over a raw `System.Net.Sockets.TcpClient` HTTP/1.1 write**, never
`WebRequest`. Reference implementation: `ZdoRedirectRunner.SendHttpPostViaSocket` —

- parse `Uri`, reject non-`http`;
- `BeginConnect` + `AsyncWaitHandle.WaitOne(timeout)` for a bounded connect (no dependency on
  `ConnectAsync` availability);
- write `POST <path> HTTP/1.1` + `Host` + `Content-Type` + `Content-Length` + `Connection: close` +
  the UTF-8 body over the `NetworkStream`;
- read the status line and **throw on non-2xx** so the caller's existing retry / `last_error` path is
  unchanged.

It runs on the poster's background `Task` thread (not the Unity main thread) and needs no prefix
registration or reflection. Client-side call sites may stay on `WebRequest` (their prefix table is
populated), but the socket helper is the mandatory path for anything that can execute on the server.

## Consequences

- **The redirect delivers server-side.** i3-w4: `posted_ok=1303`, `dropped=0`, receipts `distinct_seq`
  1..1303 contiguous — the gate that was impossible under `WebRequest`.
- **Standing rule for the remaining rungs.** I4's injection-receipt POST and I5's handshake callbacks
  must use the socket helper, not `WebRequest` — this is decided, not to be re-litigated per rung.
- **Verification discipline.** An external I/O edge is proven with a real end-to-end call from the real
  runtime, early — pre-flight a POST, not just a `/health` GET (see retro `L-2026-07-10b-3`).
- **Known latent debt.** The sibling client-side call sites share the defect but run where it is inert;
  left as-is, tracked in `DECISIONS-PENDING.md` as low-priority hardening (fold them onto the socket
  helper if any ever needs to run server-side).
- **Trade-off.** The helper is a minimal HTTP/1.1 client — no keep-alive, no chunked transfer, no TLS.
  Fine for JSON POST to the LAN gateway; revisit if server-side HTTPS or streaming is ever required.

## Related

ADR 0001, ADR 0002; `evidence/i3-redirect/ANALYSIS.md`;
`retro/SESSION-RETRO-2026-07-10.md` (lesson `L-2026-07-10b-1`); memory [[valheim-server-mono-http-trap]].
