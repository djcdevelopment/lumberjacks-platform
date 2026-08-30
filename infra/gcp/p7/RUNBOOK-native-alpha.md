# Native Northwoods alpha on P7

The first-party Lumberjacks client is the active player lane. P7 keeps Gateway, Caddy,
PostgreSQL, EventLog, Progression, and Operator API running; the historical Valheim server is
frozen in place. Its image, world, configuration, and persistent volumes are not deleted.

## Runtime contract

The live environment carries these non-secret identities:

```ini
LUMBERJACKS_NATIVE_CLIENT_RELEASE=0.1.0-alpha.1
LUMBERJACKS_NATIVE_CLIENT_DOWNLOAD_URL=https://github.com/djcdevelopment/lumberjacks-platform/releases/download/native-v0.1.0-alpha.1/Lumberjacks-0.1.0-alpha.1-windows-x64.zip
LUMBERJACKS_NATIVE_CLIENT_MAX_SESSIONS=10
LUMBERJACKS_WORLD_ID=northwoods-field-alpha
COMPOSE_PROFILES=tls
```

Gateway admits `/game` only when the caller supplies the exact client release and either a valid
per-enrollment credential or a private-plane connection. A shared compatibility key is not a
native-player credential. The field pass is delivered once, over TLS, as a no-store JSON
attachment; credentials never belong in a query string, release archive, log, screenshot, or
roadmap note.

## Cut over without risking the heirloom world

1. Cut and verify a Gateway image from the exact committed revision. Keep the currently admitted
   NetworkSense release baked into the image; this is a Gateway-only cut and does not rebuild or
   deploy the mod.
2. Back up `/etc/comfy-p7/environment`, add the four native variables above, and keep
   `COMPOSE_PROFILES=tls`.
3. Promote only Gateway. Require internal `/health`, public `/health`, release/source identity,
   anonymous-public rejection, and an authenticated native session before touching Valheim.
4. Ask Valheim to stop with its full 120-second grace period and require a fresh `World saved`
   log marker. Leave the stopped container and all volumes intact.
5. Install the committed compose control. Because `valheim-server` is behind the `valheim`
   profile, later promotions and reboots keep it frozen.
6. Exercise the authored tree loop against live P7, verify the exact resource mutation and one
   terminal `tree_felled` event in PostgreSQL, restart Gateway, and verify the fallen tree remains.

## Roll back

Rollback does not require rebuilding anything:

1. Re-pin the previous Gateway image from the promotion backup.
2. Change the one live profile line to `COMPOSE_PROFILES=tls,valheim`.
3. Start through `comfy-lumberjacks-p7.service`; wait for `Game server connected` before inviting a
   Valheim player.

Do not remove the Valheim container, world directories, image, or volumes while the native alpha
is being evaluated. Frozen means recoverable, not discarded.
