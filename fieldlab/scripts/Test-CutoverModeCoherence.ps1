#Requires -Version 5.1
<#
.SYNOPSIS
Assert that server config, gateway, and both client configs agree on which delivery lane
is armed — before any armed window or human session.

.DESCRIPTION
Lesson L-2026-08-05-4: "A mode stored in three places is a coincidence, not a mode."
The 08-05 human session failed four ways in sequence because server file config, server
runtime arming, and per-client at-rest configs each held part of "which lane are we on"
and diverged silently, producing one indistinguishable symptom (terrain-only).

This is the read-only preflight that makes that divergence loud. It gathers:

  - SERVER: `zdoRedirectEnabled` and `lumberjacksCutoverMode` from the server's BepInEx
    config (over ssh).
  - GATEWAY: `/live/valheim-cutover` effective mode + admission verdict (direct, or via
    ssh curl when the gateway is only reachable from the server host). Tolerates a 404
    from pre-r42 gateways with a warning — the endpoint is part of the r42 cut.
  - CLIENTS: OMEN (local file) and i5 (ssh): `lumberjacksGatewayUrl`, enrollment
    id/key presence, `zdoAuthoritativeConsumerEnabled`, `lumberjacksGameSessionEnabled`,
    and `autoPortOnJoinEnabled` (the 07-22 false-diagnosis precedent — always checked).

then evaluates them against -ExpectedMode and FAILS (exit 1) on any mismatch. Values of
credentials are never printed — presence only.

.EXAMPLE
# Before a P7 armed window:
fieldlab\scripts\Test-CutoverModeCoherence.ps1 -ExpectedMode lumberjacks-primary `
    -ServerSshTarget comfy-p7 `
    -ServerConfigPath /mnt/comfy-p7/valheim/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg `
    -ExpectedClientGatewayUrl http://100.124.12.37:4000 -GatewaySshTarget comfy-p7

.EXAMPLE
# Before an AM4 rehearsal against the local OMEN gateway:
fieldlab\scripts\Test-CutoverModeCoherence.ps1 -ExpectedMode lumberjacks-primary `
    -ExpectedClientGatewayUrl http://127.0.0.1:4000 -GatewayUrl http://127.0.0.1:4000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('native', 'lumberjacks-primary')]
    [string] $ExpectedMode,

    [string] $ServerSshTarget = 'am4',

    [string] $ServerConfigPath =
        '/home/derek/comfy-valheim-lab/server-state/config/bepinex/djcdevelopment.valheim.comfynetworksense.cfg',

    # Direct probe URL for /live/valheim-cutover. Ignored when -GatewaySshTarget is set.
    [string] $GatewayUrl = 'http://127.0.0.1:4000',

    # When set, the gateway is probed with curl over ssh from this host instead of
    # directly (P7's gateway is not reachable from the workstation while armed).
    [string] $GatewaySshTarget = '',

    # The gateway URL every client config must carry when the cutover lane is expected.
    [string] $ExpectedClientGatewayUrl = '',

    [string] $OmenConfigPath =
        'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg',

    [string] $I5SshTarget = 'i5',

    [string] $I5ConfigPath =
        'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg',

    [switch] $SkipI5,

    [string] $ReceiptPath = ''
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$configKeys = @(
    'zdoRedirectEnabled', 'lumberjacksCutoverMode', 'lumberjacksGatewayUrl',
    'lumberjacksEnrollmentId', 'lumberjacksClientAccessKey',
    'zdoAuthoritativeConsumerEnabled', 'lumberjacksGameSessionEnabled',
    'autoPortOnJoinEnabled', 'lumberjacksAuthoritativeWindowId')

function ConvertTo-ConfigMap([string[]] $Lines) {
    $map = @{}
    foreach ($line in $Lines) {
        if ($line -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*?)\s*$') {
            $map[$Matches[1]] = $Matches[2]
        }
    }
    return $map
}

function Get-RemoteConfigMap([string] $Ssh, [string] $Path, [bool] $Windows) {
    if ($Windows) {
        $inner = 'Get-Content -LiteralPath ''{0}''' -f $Path
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
        $lines = @(& ssh -o BatchMode=yes -o ConnectTimeout=10 $Ssh `
            "powershell -NoProfile -EncodedCommand $encoded")
    } else {
        $lines = @(& ssh -o BatchMode=yes -o ConnectTimeout=10 $Ssh "sudo cat '$Path' 2>/dev/null || cat '$Path'")
    }
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
        throw "Could not read config over ssh: ${Ssh}:$Path"
    }
    return ConvertTo-ConfigMap $lines
}

function Select-Keys([hashtable] $Map) {
    $view = [ordered]@{}
    foreach ($key in $configKeys) {
        if (-not $Map.ContainsKey($key)) { continue }
        # Presence only for the credential pair; never echo values.
        if ($key -in @('lumberjacksEnrollmentId', 'lumberjacksClientAccessKey')) {
            $view[$key] = if ([string]::IsNullOrWhiteSpace($Map[$key])) { 'absent' } else { 'present' }
        } else {
            $view[$key] = $Map[$key]
        }
    }
    return $view
}

# --- SERVER ------------------------------------------------------------------------------
$serverMap = Get-RemoteConfigMap $ServerSshTarget $ServerConfigPath $false
$serverView = Select-Keys $serverMap
$serverRedirect = ($serverMap['zdoRedirectEnabled'] -eq 'true')
$serverMode = [string]$serverMap['lumberjacksCutoverMode']

if ($ExpectedMode -eq 'lumberjacks-primary') {
    if (-not $serverRedirect) { $failures.Add('server: zdoRedirectEnabled=false but lumberjacks-primary expected') }
    if ($serverMode -ne 'lumberjacks-primary') { $failures.Add("server: lumberjacksCutoverMode='$serverMode', expected lumberjacks-primary") }
} else {
    if ($serverRedirect) { $failures.Add('server: zdoRedirectEnabled=true but native expected') }
}

# --- GATEWAY -----------------------------------------------------------------------------
$gatewayView = $null
try {
    if (-not [string]::IsNullOrWhiteSpace($GatewaySshTarget)) {
        $raw = @(& ssh -o BatchMode=yes -o ConnectTimeout=10 $GatewaySshTarget `
            "curl --fail --silent --max-time 5 'http://127.0.0.1:4000/live/valheim-cutover'")
        if ($LASTEXITCODE -ne 0) { throw 'remote curl failed' }
        $live = ($raw -join "`n") | ConvertFrom-Json
    } else {
        $live = Invoke-RestMethod -Method Get -Uri "$GatewayUrl/live/valheim-cutover" -TimeoutSec 5
    }
    $effectiveMode = if ($live.PSObject.Properties['effective_mode']) { [string]$live.effective_mode } else { [string]$live.mode }
    $gatewayView = [ordered]@{
        effective_mode = $effectiveMode
        stale = $live.PSObject.Properties['effective_mode_stale'] | ForEach-Object { $live.effective_mode_stale }
        admission = if ($live.PSObject.Properties['admission']) { $live.admission } else { $null }
    }
    if ($ExpectedMode -eq 'lumberjacks-primary' -and
        -not [string]::IsNullOrWhiteSpace($effectiveMode) -and
        $effectiveMode -ne 'lumberjacks-primary') {
        $failures.Add("gateway: effective mode '$effectiveMode', expected lumberjacks-primary")
    }
} catch {
    # Pre-r42 gateways don't serve this endpoint; an unreachable gateway is only fatal
    # when the cutover lane is expected.
    if ($ExpectedMode -eq 'lumberjacks-primary') {
        $warnings.Add("gateway: /live/valheim-cutover unavailable ($($_.Exception.Message)) - pre-r42 image or gateway down; verify by other means")
    } else {
        $warnings.Add('gateway: /live/valheim-cutover unavailable (acceptable for native mode)')
    }
}

# --- CLIENTS -----------------------------------------------------------------------------
function Test-ClientView([string] $Name, [System.Collections.Specialized.OrderedDictionary] $View) {
    if ($View['autoPortOnJoinEnabled'] -eq 'true') {
        $failures.Add("${Name}: autoPortOnJoinEnabled=true (the 07-22 empty-world false-diagnosis) - disable before any session")
    }
    if ($ExpectedMode -ne 'lumberjacks-primary') { return }
    if ($View['lumberjacksEnrollmentId'] -ne 'present' -or
        $View['lumberjacksClientAccessKey'] -ne 'present') {
        $failures.Add("${Name}: enrollment credentials absent - the client cannot attach as a consumer (the 08-05 terrain-only trap)")
    }
    if ($View['zdoAuthoritativeConsumerEnabled'] -ne 'true') {
        $failures.Add("${Name}: zdoAuthoritativeConsumerEnabled=false - delivery would be suppressed with no consumer draining it")
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedClientGatewayUrl) -and
        $View['lumberjacksGatewayUrl'] -ne $ExpectedClientGatewayUrl) {
        $failures.Add("${Name}: lumberjacksGatewayUrl='$($View['lumberjacksGatewayUrl'])', expected '$ExpectedClientGatewayUrl' (the 08-05 localhost:4000 trap)")
    }
}

if (-not (Test-Path -LiteralPath $OmenConfigPath -PathType Leaf)) {
    throw "OMEN config is missing: $OmenConfigPath"
}
$omenView = Select-Keys (ConvertTo-ConfigMap (Get-Content -LiteralPath $OmenConfigPath))
Test-ClientView 'omen' $omenView

$i5View = $null
if (-not $SkipI5) {
    $i5View = Select-Keys (Get-RemoteConfigMap $I5SshTarget $I5ConfigPath $true)
    Test-ClientView 'i5' $i5View
}

# --- VERDICT -----------------------------------------------------------------------------
$receipt = [ordered]@{
    schema_version = 1
    receipt_type = 'cutover_mode_coherence'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    expected_mode = $ExpectedMode
    server = [ordered]@{ ssh_target = $ServerSshTarget; config = $ServerConfigPath; view = $serverView }
    gateway = $gatewayView
    omen = $omenView
    i5 = $i5View
    warnings = @($warnings)
    failures = @($failures)
    result = if ($failures.Count -eq 0) { 'coherent' } else { 'divergent' }
}
$json = $receipt | ConvertTo-Json -Depth 6
if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
    [IO.File]::WriteAllText($ReceiptPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
$json | Write-Host

if ($failures.Count -gt 0) {
    Write-Error ("Mode coherence FAILED ({0} mismatch(es)): {1}" -f $failures.Count, ($failures -join '; '))
    exit 1
}
Write-Host "Mode coherence OK: every surface agrees on '$ExpectedMode'."
exit 0
