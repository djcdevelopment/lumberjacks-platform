#Requires -Version 5.1
<#
.SYNOPSIS
Run one disposable, profile-gated Valheim lab client without a keyboard.

.DESCRIPTION
This is the bounded local vertical slice:

  refresh -> stage the built DLL/config into the shared read-only client payload
  start   -> start one Compose client; the container launches Valheim and the
             opt-in LabAutoJoin patch selects an existing character
  preflight -> prove the server, Gateway, payload, install, and existing profile
               are ready before a human or MCP run is requested
  status  -> show container state and recent lifecycle lines
  stop    -> stop the client and verify the container is no longer running
  smoke   -> preflight, start, wait for the existing-character join marker, and
             always stop the client
  capture -> after the client is closed, normalize and replay the retained native
             probe through the AuthorityLab Docker build lane

After the client joins, use the existing local MCP tools to observe JSONL and
send only allowlisted mod commands. This script deliberately does not provide
arbitrary command execution inside Valheim.

The Compose client profile is the only supported auto-join environment. OMEN/i5
player installs are not modified by this script.
#>
[CmdletBinding()]
param(
    [ValidateSet('01', '02', '03', '04')]
    [string] $Client = '01',

    [ValidateSet('refresh', 'start', 'status', 'stop', 'restart', 'preflight', 'smoke', 'capture')]
    [string] $Action = 'status',

    [string] $ConfigPath = '',

    [string] $EnvFile = '',

    [ValidateRange(15, 1800)]
    [int] $WaitSeconds = 300,

    [ValidateRange(0, 600)]
    [int] $SmokeSeconds = 20,

    [string] $NativeSourcePath = '',

    [string] $CaptureOutput = '',

    # Already does what the repo-split plan calls -ModDll: supplying it skips the in-place
    # ComfyNetworkSense build (Refresh-Payload below) and stages this artifact instead,
    # hash-logged via Copy-Verified. -ModDll is an alias so a future release-artifact caller
    # can name it that way without changing behavior; omitted, current behavior is unchanged.
    [Alias('ModDll')]
    [string] $DllPath = '',

    [switch] $AllowUnseeded,

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$composeFile = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.compose.yml'
$defaultEnv = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.env'
$exampleEnv = Join-Path $repoRoot 'fieldlab\autonomous\valheim-lab.env.example'
$sharedRoot = Join-Path $repoRoot 'fieldlab\autonomous\state\client-shared'
$dllSource = if ([string]::IsNullOrWhiteSpace($DllPath)) {
    Join-Path $repoRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
} elseif ([IO.Path]::IsPathRooted($DllPath)) {
    [IO.Path]::GetFullPath($DllPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $DllPath))
}
$clientInitSource = Join-Path $repoRoot 'fieldlab\autonomous\client-init\20-comfy-valheim-autostart.sh'
$clientService = "valheim-client-$Client"
$clientHome = Join-Path $repoRoot "fieldlab\autonomous\state\client$Client\home"
$clientGames = Join-Path $repoRoot "fieldlab\autonomous\state\client$Client\games"
$clientInstall = Join-Path $clientGames 'GameLibrary\Steam\steamapps\common\Valheim'
$clientLog = Join-Path $clientInstall 'BepInEx\LogOutput.log'
$gateReceiptPath = Join-Path $repoRoot "fieldlab\autonomous\state\client$Client\lab-preflight.json"
$smokeReceiptPath = Join-Path $repoRoot "fieldlab\autonomous\state\client$Client\lab-smoke.json"

if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) { throw "Compose file not found: $composeFile" }
if (-not $EnvFile) { $EnvFile = if (Test-Path -LiteralPath $defaultEnv -PathType Leaf) { $defaultEnv } else { $exampleEnv } }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw "Compose env file not found: $EnvFile" }

function Invoke-Compose([string[]] $Arguments) {
    & docker compose --env-file $EnvFile -f $composeFile --profile clients @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed: $($Arguments -join ' ')" }
}

function Copy-Verified([string] $Source, [string] $Target) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "payload file not found: $Source" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw "payload verification failed: $Target" }
    Write-Host ("staged {0} sha256:{1}" -f $Target, $sourceHash.Substring(0, 12).ToLowerInvariant())
}

function Refresh-Payload {
    if (-not $NoBuild -and [string]::IsNullOrWhiteSpace($DllPath)) {
        Push-Location $repoRoot
        try {
            & dotnet build '.\network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj' -c Release
            if ($LASTEXITCODE -ne 0) { throw 'ComfyNetworkSense build failed' }
        } finally { Pop-Location }
    }
    Copy-Verified $dllSource (Join-Path $sharedRoot 'plugins\ComfyNetworkSense.dll')
    if ($ConfigPath) {
        $resolvedConfig = (Resolve-Path $ConfigPath).Path
        Copy-Verified $resolvedConfig (Join-Path $sharedRoot 'config\djcdevelopment.valheim.comfynetworksense.cfg')
    } else {
        Write-Host 'no config staged; disposable clients use their existing/default config'
    }
    $routeSource = Join-Path $repoRoot 'network\mod\ComfyNetworkSense\bin\Release\teleport-route.tsv'
    if (Test-Path -LiteralPath $routeSource -PathType Leaf) {
        Copy-Verified $routeSource (Join-Path $sharedRoot 'comfy-network-sense\teleport-route.tsv')
    }
}

function Stage-ClientInit {
    New-Item -ItemType Directory -Force -Path (Join-Path $clientHome 'init.d') | Out-Null
    Copy-Verified $clientInitSource (Join-Path $clientHome 'init.d\20-comfy-valheim-autostart.sh')
}

function Write-JsonReceipt([string] $Path, [object] $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $temporary = "$Path.tmp"
    ($Value | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function New-PreflightCheck([string] $Name, [bool] $Passed, [string] $Detail, [bool] $Required = $true) {
    [pscustomobject]@{
        name = $Name
        passed = $Passed
        required = $Required
        detail = $Detail
    }
}

function Select-CheckDetail([bool] $Passed, [string] $Pass, [string] $Fail) {
    if ($Passed) { return $Pass }
    return $Fail
}

function Select-CheckLabel([bool] $Passed) {
    if ($Passed) { return 'PASS' }
    return 'BLOCK'
}

function Invoke-LabPreflight {
    $checks = @()

    $dockerInfo = & docker info --format '{{.ServerVersion}}' 2>$null
    $dockerReady = ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($dockerInfo -join ' ')))
    $checks += New-PreflightCheck 'docker_runtime' $dockerReady (Select-CheckDetail $dockerReady "Docker server $($dockerInfo -join ' ')" 'Docker server is unavailable to this shell')

    $composeConfig = & docker compose --env-file $EnvFile -f $composeFile --profile clients config --quiet 2>$null
    $composeReady = ($LASTEXITCODE -eq 0)
    $checks += New-PreflightCheck 'compose_contract' $composeReady (Select-CheckDetail $composeReady 'Compose file and environment expand successfully' 'docker compose config failed')

    $stagedDll = Join-Path $sharedRoot 'plugins\ComfyNetworkSense.dll'
    $sourceReady = Test-Path -LiteralPath $dllSource -PathType Leaf
    $stagedReady = Test-Path -LiteralPath $stagedDll -PathType Leaf
    $sourceHash = if ($sourceReady) { (Get-FileHash -LiteralPath $dllSource -Algorithm SHA256).Hash } else { '' }
    $stagedHash = if ($stagedReady) { (Get-FileHash -LiteralPath $stagedDll -Algorithm SHA256).Hash } else { '' }
    $dllReady = $sourceReady -and $stagedReady -and $sourceHash -eq $stagedHash
    $dllDetail = if ($dllReady) {
        "shared ComfyNetworkSense.dll matches source sha256:$($sourceHash.Substring(0, 12).ToLowerInvariant())"
    } elseif (-not $sourceReady) {
        "DLL source is missing: $dllSource"
    } elseif (-not $stagedReady) {
        'shared ComfyNetworkSense.dll is missing; run refresh'
    } else {
        "shared DLL hash $($stagedHash.Substring(0, 12).ToLowerInvariant()) does not match requested source $($sourceHash.Substring(0, 12).ToLowerInvariant()); run refresh"
    }
    $checks += New-PreflightCheck 'mod_payload' $dllReady $dllDetail

    $initPath = Join-Path $clientHome 'init.d\20-comfy-valheim-autostart.sh'
    $initReady = Test-Path -LiteralPath $initPath -PathType Leaf
    $checks += New-PreflightCheck 'client_lifecycle_script' $initReady (Select-CheckDetail $initReady 'writable client init script is staged' 'client init script is missing; run refresh')

    $homeReady = Test-Path -LiteralPath $clientHome -PathType Container
    $checks += New-PreflightCheck 'client_home' $homeReady (Select-CheckDetail $homeReady $clientHome "client home is missing: $clientHome")

    $installReady = Test-Path -LiteralPath $clientInstall -PathType Container
    $checks += New-PreflightCheck 'valheim_install' $installReady (Select-CheckDetail $installReady $clientInstall "Valheim install is not seeded: $clientInstall")

    $manifestPath = Join-Path $clientInstall 'steamapps\appmanifest_892970.acf'
    $manifestReady = Test-Path -LiteralPath $manifestPath -PathType Leaf
    $checks += New-PreflightCheck 'steam_valheim_manifest' $manifestReady (Select-CheckDetail $manifestReady 'Steam manifest for App 892970 exists' 'Steam Valheim manifest is missing; the disposable volume needs a one-time Steam install/login')

    $binary = @(
        (Join-Path $clientInstall 'valheim.x86_64'),
        (Join-Path $clientInstall 'valheim.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    $binaryReady = -not [string]::IsNullOrWhiteSpace($binary)
    $checks += New-PreflightCheck 'valheim_binary' $binaryReady (Select-CheckDetail $binaryReady "Valheim executable exists: $binary" 'Valheim executable is missing from the seeded install')

    $profiles = @()
    if ($homeReady) {
        $profiles = @(Get-ChildItem -LiteralPath $clientHome -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.fch', '.fch.old') } | Select-Object -First 1)
    }
    $profileReady = $profiles.Count -gt 0
    $checks += New-PreflightCheck 'existing_character_profile' $profileReady (Select-CheckDetail $profileReady "existing profile found: $($profiles[0].Name)" 'no existing Valheim character profile found; auto-join intentionally will not create one')

    $runningNames = @(& docker ps --format '{{.Names}}' 2>$null)
    $serverReady = @($runningNames | Where-Object { $_ -match 'valheim-server' }).Count -gt 0
    $gatewayReady = @($runningNames | Where-Object { $_ -match 'comfy-gateway' }).Count -gt 0
    $checks += New-PreflightCheck 'local_valheim_server' $serverReady (Select-CheckDetail $serverReady 'local Valheim server container is running' 'local Valheim server container is not running')
    $checks += New-PreflightCheck 'local_mcp_gateway' $gatewayReady (Select-CheckDetail $gatewayReady 'local Comfy MCP Gateway container is running' 'local Comfy MCP Gateway container is not running')

    $blockers = @($checks | Where-Object { $_.required -and -not $_.passed } | ForEach-Object { $_.name })
    $result = if ($blockers.Count -eq 0) { 'ready' } else { 'blocked' }
    $oneTimeSeedNeeded = @(@('valheim_install', 'steam_valheim_manifest', 'valheim_binary', 'existing_character_profile') |
        Where-Object { $blockers -contains $_ }
    )
    $receipt = [ordered]@{
        schema_version = 1
        receipt_type = 'lab_operator_touch_gate'
        client = $clientService
        generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
        result = $result
        operator_touch_allowed = ($result -eq 'ready')
        one_time_seed_required = ($oneTimeSeedNeeded.Count -gt 0)
        blockers = $blockers
        checks = @($checks)
        next_action = if ($result -eq 'ready') { 'Agent may start the client, use bounded MCP commands, collect telemetry, and stop it.' } else { 'Seed the disposable volume once, then rerun preflight; no player login is requested by this gate.' }
    }
    Write-JsonReceipt $gateReceiptPath $receipt
    foreach ($check in $checks) {
        Write-Host ("{0} {1}: {2}" -f (Select-CheckLabel $check.passed), $check.name, $check.detail)
    }
    Write-Host "preflight $result -> $gateReceiptPath"
    [pscustomobject]@{ Ready = ($result -eq 'ready'); Receipt = $receipt; ClientInstall = $clientInstall }
}

function Start-LabClient {
    New-Item -ItemType Directory -Force -Path $sharedRoot | Out-Null
    Stage-ClientInit
    if (-not (Test-Path -LiteralPath (Join-Path $sharedRoot 'plugins\ComfyNetworkSense.dll') -PathType Leaf)) {
        Refresh-Payload
    }
    Invoke-Compose @('up', '-d', $clientService)
    Write-Host "started $clientService"
}

function Stop-LabClient {
    Invoke-Compose @('stop', '-t', '60', $clientService)
    $running = (& docker ps --filter "name=$clientService" --filter 'status=running' --format '{{.Names}}')
    if ($running) { throw "client container is still running after graceful stop: $running" }
    Write-Host "stopped $clientService"
}

function Wait-ForAutoJoin([int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $pattern = 'Lab auto-join started existing character'
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $clientLog -PathType Leaf) {
            $match = Get-Content -LiteralPath $clientLog -Tail 240 -ErrorAction SilentlyContinue | Select-String -Pattern $pattern | Select-Object -Last 1
            if ($match) {
                return [pscustomobject]@{ Ready = $true; Marker = $match.Line; Log = $clientLog }
            }
        }
        Start-Sleep -Seconds 5
    }
    [pscustomobject]@{ Ready = $false; Marker = $null; Log = $clientLog }
}

function Invoke-AuthorityLabDocker([string[]] $Arguments) {
    $dockerVolume = "$repoRoot`:/repo"
    & docker run --rm -v $dockerVolume -w /repo/tools/authority-lab mcr.microsoft.com/dotnet/sdk:9.0 dotnet run --project src/AuthorityLab @Arguments
    if ($LASTEXITCODE -ne 0) { throw "AuthorityLab Docker command failed: $($Arguments -join ' ')" }
}

function Capture-NativeEvidence {
    $running = (& docker ps --filter "name=$clientService" --filter 'status=running' --format '{{.Names}}')
    if ($running) { throw "capture requires $clientService to be stopped; close the client first so JSONL is flushed" }

    $source = $null
    if ($NativeSourcePath) {
        $source = (Resolve-Path -LiteralPath $NativeSourcePath -ErrorAction Stop).Path
    } else {
        $candidates = @(
            (Join-Path $clientInstall 'BepInEx\config\comfy-network-sense\netcode-probe.jsonl'),
            (Join-Path $clientInstall 'BepInEx\config\netcode-probe.jsonl'),
            (Join-Path $repoRoot 'fieldlab\autonomous\state\server\config\bepinex\comfy-network-sense\netcode-probe.jsonl'),
            (Join-Path $repoRoot 'fieldlab\autonomous\state\server\config\bepinex\config\comfy-network-sense\netcode-probe.jsonl')
        )
        $source = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $source -or -not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw 'no native probe JSONL found; run a closed client/server probe window before capture'
    }

    $resolvedSource = (Resolve-Path -LiteralPath $source).Path
    $repoPrefix = $repoRoot.TrimEnd('\') + '\'
    if (-not $resolvedSource.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "native source must be inside the repository root: $resolvedSource"
    }
    $sourceInRepo = '/repo/' + ($resolvedSource.Substring($repoPrefix.Length) -replace '\\', '/')
    $scenarioInRepo = '/repo/fieldlab/experiments/m7/m7-e04-native-candidate-capture/scenario.yaml'
    if (-not $CaptureOutput) {
        $CaptureOutput = Join-Path $repoRoot ('fieldlab\experiments\m7\m7-e04-native-candidate-capture\runs\native-client' + $Client + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    $resolvedOutput = [IO.Path]::GetFullPath($CaptureOutput)
    New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
    $outputPrefix = $repoRoot.TrimEnd('\') + '\'
    if (-not $resolvedOutput.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "capture output must be inside the repository root: $resolvedOutput"
    }
    $outputInRepo = '/repo/' + ($resolvedOutput.Substring($outputPrefix.Length) -replace '\\', '/')
    $replayOutput = Join-Path $resolvedOutput 'replay'
    $replayInRepo = $outputInRepo + '/replay'

    Write-Host "normalizing native evidence: $resolvedSource"
    Invoke-AuthorityLabDocker @('normalize-native', '--scenario', $scenarioInRepo, '--input', $sourceInRepo, '--output', $outputInRepo)
    Write-Host "replaying native candidates through the current Lumberjacks policy"
    Invoke-AuthorityLabDocker @('replay-native', '--run', $outputInRepo, '--output', $replayInRepo)
    Write-Host "native evidence captured -> $resolvedOutput"
}

switch ($Action) {
    'refresh' {
        Stage-ClientInit
        Refresh-Payload
        break
    }
    'start' {
        $gate = Invoke-LabPreflight
        if (-not $gate.Ready -and -not $AllowUnseeded) { throw "operator-touch gate blocked start; see $gateReceiptPath (use -AllowUnseeded only for the one-time disposable-volume seed)" }
        Start-LabClient
        Write-Host 'inspect lifecycle with -Action status, then use bounded MCP telemetry/commands and stop when finished'
        break
    }
    'restart' {
        $gate = Invoke-LabPreflight
        if (-not $gate.Ready -and -not $AllowUnseeded) { throw "operator-touch gate blocked restart; see $gateReceiptPath" }
        Stop-LabClient
        Refresh-Payload
        Start-LabClient
        Write-Host 'restarted with verified shared payload'
        break
    }
    'stop' {
        Stop-LabClient
        break
    }
    'preflight' {
        $gate = Invoke-LabPreflight
        if (-not $gate.Ready) { exit 3 }
        break
    }
    'capture' {
        Capture-NativeEvidence
        break
    }
    'smoke' {
        $gate = Invoke-LabPreflight
        if (-not $gate.Ready) { throw "operator-touch gate blocked smoke; see $gateReceiptPath" }
        $started = $false
        $startedUtc = (Get-Date).ToUniversalTime().ToString('o')
        $smokeResult = 'failed'
        $ready = $null
        try {
            Start-LabClient
            $started = $true
            $ready = Wait-ForAutoJoin $WaitSeconds
            if (-not $ready.Ready) { throw "auto-join marker not observed within $WaitSeconds seconds: $clientLog" }
            Start-Sleep -Seconds $SmokeSeconds
            $smokeResult = 'joined_and_held'
            Write-Host "smoke observed existing-character auto-join; held for $SmokeSeconds second(s)"
        } finally {
            if ($started) { Stop-LabClient }
            Write-JsonReceipt $smokeReceiptPath ([ordered]@{
                schema_version = 1
                receipt_type = 'lab_smoke'
                client = $clientService
                started_at_utc = $startedUtc
                ended_at_utc = (Get-Date).ToUniversalTime().ToString('o')
                result = $smokeResult
                auto_join = $ready
                preflight = $gate.Receipt
                closed_after_run = $true
            })
        }
        break
    }
    'status' {
        Invoke-Compose @('ps', $clientService)
        Invoke-Compose @('logs', '--tail', '80', $clientService)
        break
    }
}
