# Valheim dedicated-server runtime control

Use this lane for the small networking rollback allowlist that must change without
reloading the world. It is not a console bridge and does not accept arbitrary BepInEx
keys.

## Preconditions

- `serverRuntimeControlEnabled = true` under `[ServerControl]` in the server's
  ComfyNetworkSense config.
- BatchMode SSH to the host already succeeds.
- The server is running a build containing `ServerRuntimeControlRunner`.

The trust boundary is the existing authenticated host login. The mod opens no port.
The caller stages one JSON file and atomically moves it into the mounted BepInEx
config directory.

## Allowlist

- `zdoRedirectEnabled`
- `zdoCoPresenceShadowEnabled`
- `zdoCoPresenceFanoutEnabled`
- `handshakeResponderEnabled`
- `handshakeResponderStrictMode`
- `handshakeResponderEndpoint`
- `handshakeResponderWindowId`
- `nativeNetworkPoisonEnabled`
- `nativeNetworkEvidenceRunId`

Boolean settings accept `true` or `false`. The endpoint accepts plain HTTP without
userinfo. The window ID accepts an 80-character-or-shorter safe token.

## Apply and verify on AM4

```powershell
fieldlab\scripts\Invoke-ValheimServerRuntimeControl.ps1 `
  -Setting zdoCoPresenceFanoutEnabled `
  -Value false
```

The command succeeds only after it reads the matching row from
`comfy-network-sense/runtime-control-receipts.jsonl`. The receipt contains the request
ID, allow-listed setting, requested value, old value, effective in-process value, and
the direct effect taken by the owning runner.

For P7, keep the same script and point it at the mounted config root:

```powershell
fieldlab\scripts\Invoke-ValheimServerRuntimeControl.ps1 `
  -SshTarget comfy-p7 `
  -RemoteBepInExConfigRoot /mnt/comfy-p7/valheim/config/bepinex `
  -Setting zdoCoPresenceFanoutEnabled `
  -Value false
```

This invocation does not authorize a P7 deployment or a cloud mutation. The target
server must already contain the runtime-control build.

## Failure behavior

- Disabled lane or stopped server: no receipt; the caller times out.
- Unsupported setting, invalid value, malformed schema, or duplicate request ID:
  a `refused` receipt is written and the script exits nonzero.
- Disabling an armed ZDO redirect invokes its normal stop path immediately.
- Changing the handshake endpoint or window disarms and re-arms the responder
  in-process. Any request already pending under the old generation is passed through
  to vanilla rather than enforcing a stale verdict.
