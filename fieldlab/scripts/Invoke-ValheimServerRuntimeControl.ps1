#Requires -Version 5.1
<#
.SYNOPSIS
Atomically submit one allow-listed ComfyNetworkSense runtime setting to a dedicated server.

.DESCRIPTION
The mod exposes no network listener and no general console bridge. This script relies on the
operator's existing BatchMode SSH authentication, stages one JSON command in the server's mounted
BepInEx config directory, and waits for the mod's machine-readable receipt.

.EXAMPLE
.\Invoke-ValheimServerRuntimeControl.ps1 `
  -Setting zdoCoPresenceFanoutEnabled `
  -Value false
#>
[CmdletBinding()]
param(
    [ValidateSet(
        'zdoRedirectEnabled',
        'zdoCoPresenceShadowEnabled',
        'zdoCoPresenceFanoutEnabled',
        'handshakeResponderEnabled',
        'handshakeResponderStrictMode',
        'handshakeResponderEndpoint',
        'handshakeResponderWindowId',
        'nativeNetworkPoisonEnabled',
        'nativeNetworkEvidenceRunId',
        'directControlCutoverEnabled')]
    [Parameter(Mandatory)]
    [string] $Setting,

    [Parameter(Mandatory)]
    [string] $Value,

    [string] $SshTarget = 'am4',

    [string] $RemoteBepInExConfigRoot =
        '/home/derek/comfy-valheim-lab/server-state/config/bepinex',

    [string] $RequestId = '',

    [ValidateRange(2, 60)]
    [int] $WaitSeconds = 15
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RequestId)) {
    $RequestId = 'runtime-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') +
        '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}
if ($RequestId.Length -gt 80 -or $RequestId -notmatch '^[A-Za-z0-9._-]+$') {
    throw "RequestId must be an 80-character-or-shorter safe token: $RequestId"
}
if ($SshTarget -notmatch '^[A-Za-z0-9._-]+$') {
    throw "SshTarget must be an SSH alias or hostname token: $SshTarget"
}
if (-not $RemoteBepInExConfigRoot.StartsWith('/') -or
    $RemoteBepInExConfigRoot -match "[`r`n'`"]" -or
    -not $RemoteBepInExConfigRoot.EndsWith('/bepinex')) {
    throw 'RemoteBepInExConfigRoot must be an absolute path ending in /bepinex.'
}

$controlDirectory = "$RemoteBepInExConfigRoot/comfy-network-sense"
$remoteFinal = "$controlDirectory/runtime-control.json"
$remoteStaged = "$controlDirectory/runtime-control.json.new-$RequestId"
$remoteReceipts = "$controlDirectory/runtime-control-receipts.jsonl"
$temporaryPath = Join-Path ([IO.Path]::GetTempPath()) (
    'baseline-valheim-runtime-control-' + [Guid]::NewGuid().ToString('N') + '.json')

$command = [ordered]@{
    schema_version = 1
    request_id = $RequestId
    setting = $Setting
    value = $Value
}

try {
    [IO.File]::WriteAllText(
        $temporaryPath,
        (($command | ConvertTo-Json -Compress) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))

    & ssh -o BatchMode=yes $SshTarget "install -d -m 700 '$controlDirectory'"
    if ($LASTEXITCODE -ne 0) {
        throw "ssh failed while preparing the runtime-control directory (exit $LASTEXITCODE)."
    }

    & scp -q -- $temporaryPath "${SshTarget}:$remoteStaged"
    if ($LASTEXITCODE -ne 0) {
        throw "scp failed while staging runtime command (exit $LASTEXITCODE)."
    }

    $publish = "set -eu; mv '$remoteStaged' '$remoteFinal'"
    & ssh -o BatchMode=yes $SshTarget $publish
    if ($LASTEXITCODE -ne 0) {
        throw "ssh failed while publishing runtime command (exit $LASTEXITCODE)."
    }

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $receipt = $null
    do {
        Start-Sleep -Milliseconds 500
        $receiptOutput = @(& ssh -o BatchMode=yes $SshTarget "tail -n 64 '$remoteReceipts' 2>/dev/null")
        if ($LASTEXITCODE -notin @(0, 1)) {
            throw "ssh failed while reading runtime receipts (exit $LASTEXITCODE)."
        }
        foreach ($line in $receiptOutput) {
            try {
                $candidate = $line | ConvertFrom-Json -ErrorAction Stop
                if ($candidate.event -eq 'server_runtime_control' -and
                    $candidate.request_id -eq $RequestId) {
                    $receipt = $candidate
                }
            } catch {
                # Ignore non-JSON SSH/banner output and an incomplete first line from tail.
            }
        }
    } until ($receipt -or (Get-Date) -ge $deadline)
    if (-not $receipt) {
        throw "No runtime-control receipt for $RequestId within $WaitSeconds seconds."
    }

    $receipt | ConvertTo-Json -Depth 8
    if ($receipt.result -ne 'applied') {
        exit 4
    }
} finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
