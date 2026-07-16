# P7 volunteer enrollment and direct Gateway

Status: the one-time Steam enrollment flow and authenticated direct client route are
live. OMEN completed the 2026-07-16 single-client authoritative-priority gate through
this route without an SSH tunnel.

## Flow

```text
admin on OMEN
  -> SSH/IAP command creates a random, one-use, 24-hour invite on GCP
  -> volunteer opens the returned link
  -> Steam OpenID verifies the Steam account
  -> Gateway redeems the invite once
  -> Gateway issues enrollment id + per-player access token
  -> volunteer configures ComfyNetworkSense
  -> mod authenticates redirect/poll/ack/telemetry requests to GCP :42317
```

The admin key is read and used on GCP; it is not returned to OMEN. The invite token
is not a client credential and cannot be redeemed twice. Enrollment data is persisted
under `/mnt/comfy-p7/lumberjacks/enrollment/`.

## Administrator: generate one invite

```powershell
& C:\work\comfy\infra\gcp\p7\scripts\new-player-invite.ps1
```

Send only the newly returned `invite_url` to the intended player. It expires after 24
hours and is single-use. Generate a separate invite for every Steam account.

## Player: redeem and configure

1. Open the invite URL.
2. Select **Sign in with Steam** and complete Steam's OpenID prompt.
3. Copy the returned `[Lumberjacks]` block into
   `Valheim\BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg`.
4. Ensure `zdoAuthoritativeConsumerEnabled = true` under `[Lumberjacks]`.
5. Close and restart Valheim so BepInEx reloads the config and plugin.

The result has this shape; every `<issued ...>` value is private to that player:

```ini
[Lumberjacks]
lumberjacksGatewayUrl = http://8.231.129.249:42317
lumberjacksAuthoritativeWindowId = p7-primary-v1
lumberjacksEnrollmentId = <issued enrollment id>
lumberjacksClientAccessKey = <issued access token>
zdoAuthoritativeConsumerEnabled = true
```

Do not publish the callback response, commit the config, or include the access token in
screenshots, reports, logs, or chat. If it is exposed, issue a new enrollment and
remove/revoke the old record before using that account again.

## Preflight and launch

```powershell
$gateway = 'http://8.231.129.249:42317'
Invoke-RestMethod "$gateway/health"

& C:\work\comfy\infra\gcp\p7\scripts\start-direct-session.ps1
```

The launch helper verifies Gateway health and starts Valheim with
`+connect 8.231.129.249:2456`. The authoritative poller lives inside
ComfyNetworkSense; no OMEN forwarding process or per-player SSH tunnel is required.

During a primary test, the administrator watches:

```powershell
Invoke-RestMethod `
  http://8.231.129.249:42317/api/v0/telemetry/cutover |
  ConvertTo-Json -Depth 20
```

Admit only one client while P7 still uses the shared `p7-primary-v1` queue. A session
passes when it reaches 100% coverage, zero native-only/fallback, receipts equal
acknowledgements, pending zero, `complete=true`, and zero reject/duplicate/retry/client
transport failures.

## Security and rollout boundary

- Authoritative Valheim routes accept a valid per-enrollment ID/token pair. A shared
  fallback client key may still exist for operator compatibility but is not the normal
  volunteer credential.
- The current endpoint is plain HTTP on a non-default port. Dashboard GET routes are
  not access-controlled. This is acceptable only for the explicitly limited pilot.
- Before wider public use, add TLS, rate limiting, credential revocation/rotation,
  access logging, and separation of public telemetry from control paths.
- Before simultaneous volunteers, make queue delivery and acknowledgements
  recipient-scoped and pass a two-real-client isolation test. Authentication alone
  does not make today's shared queue multiplayer-correct.

## Operator fallback

If the pilot port is intentionally closed, OMEN can still use an SSH/IAP loopback
tunnel at `127.0.0.1:14000`. That is an operator recovery route, not the scalable
player topology and not a requirement for the successful direct P7 path.
