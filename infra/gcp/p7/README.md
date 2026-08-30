# Lumberjacks P7 environment

Status: **native Northwoods field alpha live**, 2026-08-30 UTC.

P7 runs the native Lumberjacks Gateway, PostgreSQL, Caddy, EventLog, Progression,
and Operator API. The standalone Godot client owns the active game loop. Gateway
`m33-native-20260830-r1` admits the exact native client `0.1.0-alpha.1` for an
invite-only cohort capped at ten sessions.

The public Windows x64 prerelease is published with SHA-256
`4dd6e02a08308487d006ac4868f178933c672f0aec4b7652744696033db026bf` and a
verified GitHub attestation. The authored Storm Pine loop persisted through a
Gateway restart and a full VM stop/start on `e2-medium`.

The historical Valheim server saved cleanly and is frozen as rollback evidence. It
is excluded from the default `tls` compose profile and is restored only by the
explicit `tls,valheim` rollback profile. No world, image, configuration, or state
was deleted.

See:

- [`RUNBOOK-native-alpha.md`](RUNBOOK-native-alpha.md) for cutover and rollback.
- [The deployment receipt](../../../Lumberjacks/docs/roadmap/native-v0.1.0-alpha.1-deployment.json)
  for exact release, image, live-loop, and persistence observations.
- [The public prerelease](https://github.com/djcdevelopment/lumberjacks-platform/releases/tag/native-v0.1.0-alpha.1)
  for the Windows archive, checksum, and attestation.

## Live deployment

| Item | Value |
|---|---|
| GCP project | `lumberjacks-exp-20260711-djc` |
| VM / zone | `comfy-lumberjacks-p7` / `us-west1-b` |
| Machine | `e2-medium` |
| SSH target | `comfy-p7` through IAP |
| Persistent disk | `/mnt/comfy-p7` |
| World | `northwoods-field-alpha` |
| Public edge | Caddy TLS; Gateway remains internal on container port `4000` |
| Native session cap | `10` |
| Native release | `native-v0.1.0-alpha.1` |
| Native source | `45bc6935dbb393c623baeef164064344df4c492e` |
| Gateway release | `m33-native-20260830-r1` |
| Gateway image | `sha256:02807e680d27b23cb51f0b5c9e15cd8d0cb4128c733d8ca156e1202d091f9383` |
| Frozen Valheim image | `ghcr.io/community-valheim-tools/valheim-server@sha256:e8b13da3c44f54a38511c8ac224f2959a437c0b2626cf916683ca7acc8dfb146` |

The environment file is `/etc/comfy-p7/environment` with mode `0600`. Do not print
it wholesale: it contains database, telemetry, and administration secrets. The
active non-secret declarations are:

```ini
LUMBERJACKS_NATIVE_CLIENT_RELEASE=0.1.0-alpha.1
LUMBERJACKS_NATIVE_CLIENT_DOWNLOAD_URL=https://github.com/djcdevelopment/lumberjacks-platform/releases/download/native-v0.1.0-alpha.1/Lumberjacks-0.1.0-alpha.1-windows-x64.zip
LUMBERJACKS_NATIVE_CLIENT_MAX_SESSIONS=10
LUMBERJACKS_WORLD_ID=northwoods-field-alpha
COMPOSE_PROFILES=tls
```

`/game` requires the exact client release plus either a valid per-enrollment
credential or a private-plane connection. A shared compatibility key is rejected
for native players. A field pass is a one-use, no-store JSON attachment delivered
over TLS; never put its credential in a URL, release archive, log, screenshot, or
roadmap note.

## Operational checks

Before inviting a player, require:

1. Public `/health` reports healthy and `/identity` reports Gateway
   `m33-native-20260830-r1` from source
   `45bc6935dbb393c623baeef164064344df4c492e`.
2. An anonymous valid WebSocket upgrade to `/game` is rejected with HTTP 401.
3. The active compose profile is `tls` and the Valheim container remains exited.
4. The public archive checksum matches the value above.
5. The invite creates a one-use native field pass for release `0.1.0-alpha.1`.

The complete local and P7 field-loop proof felled `northwoods-old-pine` in eight
strikes, persisted health `0`, recorded fall heading `-2.3`, and emitted exactly
one `tree_felled` event. Recheck those invariants after any Gateway promotion or
machine restart.

## Deploy and roll back

Gateway deploys promote a prebuilt, locally verified image. Do not copy Gateway
source to P7 and do not run `docker compose build` there. Use the guarded scripts
in [`scripts`](scripts), which assert repository identity, back up the environment,
promote with `--no-build --no-deps`, and verify health and the exact running image.

Rollback does not rebuild or delete anything:

1. Re-pin `lumberjacks-gateway:m32-creatoros-20260830-r3` using the promotion
   backup at
   `/mnt/comfy-p7/backups/gateway-image-promote/20260830T163747Z/environment`.
2. Set `COMPOSE_PROFILES=tls,valheim`.
3. Start through `comfy-lumberjacks-p7.service` and wait for
   `Game server connected` before inviting a Valheim player.

The native-control backup is
`/mnt/comfy-p7/backups/native-alpha/20260830T163735Z`. Frozen means recoverable,
not discarded.

## Historical evidence

The 2026-07-16 and 2026-07-21 Valheim/NetworkSense acceptance windows remain
valuable transport evidence, but they are no longer the active product topology.
Their receipts and manifests remain under `Lumberjacks/docs/roadmap` and
`fieldlab/evidence`; the native alpha did not rebuild or modify their binaries.

## Remaining physical proof

The i5 third machine was offline during release. Before broadening the cohort,
install and launch the published archive there and complete one real public
Steam-authenticated invitation, field-pass import, and human play lap. These two
checks are intentionally recorded as unverified, not inferred from private-plane
automation.
