<#
.SYNOPSIS
Runs the bounded, allow-listed host side of the local Workbench.

.DESCRIPTION
The Companion web process never receives the Docker socket and never runs a shell.
This process is launched by the local Companion launcher as the current interactive
Windows user. It polls only the typed Workbench job queue, dispatches known capability
IDs, and posts bounded phase/receipt updates back to Companion over loopback.

This is intentionally not a general-purpose task runner. Unknown capability IDs fail
closed and arbitrary command/path arguments are ignored.
#>
[CmdletBinding()]
param(
    [string]$CompanionUrl = 'http://127.0.0.1:8080',
    [string]$ContainerName = 'lumberjacks-companion-companion-1',
    [string]$ProjectName = 'lumberjacks-companion',
    [string]$ComposeFile = '',
    [string]$RepoRoot = '',
    [string]$ValheimPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [ValidateSet('Explore','Admin','Dev','Lab','Production')]
    [string]$Profile = '',
    [string]$RunnerId = '',
    [ValidateRange(1, 30)][int]$PollSeconds = 2,
    [switch]$ReplaceExisting,
    [switch]$Once
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:RunnerLogRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Lumberjacks\Workbench'
New-Item -ItemType Directory -Force -Path $script:RunnerLogRoot | Out-Null
$script:RunnerLogPath = Join-Path $script:RunnerLogRoot 'runner.log'
function Write-RunnerDiagnostic {
    param([string]$Message)
    try { Add-Content -LiteralPath $script:RunnerLogPath -Value ("{0} {1}" -f [DateTimeOffset]::UtcNow.ToString('O'), $Message) } catch { }
}
trap {
    Write-RunnerDiagnostic ("fatal: " + $_.Exception.Message)
    break
}

if ($ReplaceExisting) {
    # A runner loads this script once and then remains in its poll loop. Rebuilding
    # containers or changing profile does not update that in-memory function set.
    # Replace only PowerShell processes whose -File argument is this exact script;
    # never terminate a generic shell or a runner from another checkout/bundle.
    $runnerPattern = '(?i)-File\s+"?' + [regex]::Escape([IO.Path]::GetFullPath($PSCommandPath)) + '(?:"|\s|$)'
    $existing = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessId -ne $PID -and
            $_.Name -in @('powershell.exe', 'pwsh.exe') -and
            $_.CommandLine -match $runnerPattern
        })
    foreach ($process in $existing) {
        Write-RunnerDiagnostic ("replacing runner pid {0} for profile {1}" -f $process.ProcessId, $Profile)
        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
}
$script:RunnerMutex = [Threading.Mutex]::new($false, 'Local\BaselineWorkbenchHostRunner')
if (-not $script:RunnerMutex.WaitOne(0)) { exit 0 }

if ([string]::IsNullOrWhiteSpace($RunnerId)) {
    $RunnerId = "runner-$env:COMPUTERNAME".ToLowerInvariant()
}
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
}
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = (Resolve-Path (Join-Path $PSScriptRoot 'docker-compose.yml')).Path
}
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$ComposeFile = [IO.Path]::GetFullPath($ComposeFile)
$CompanionUrl = $CompanionUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($Profile)) { $Profile = [string]$env:LUMBERJACKS_WORKBENCH_PROFILE }
if ([string]::IsNullOrWhiteSpace($Profile)) { $Profile = 'Explore' }

function Get-RunnerToken {
    # Resolving the Workbench security projection constructs its persistent store
    # before the host process attempts to read the runner-only token.
    try { Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/security" -Method Get -TimeoutSec 10 | Out-Null } catch { }
    $token = & docker exec $ContainerName sh -lc 'cat /run/workbench/runner-token 2>/dev/null || cat /data/workbench/runner-token' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($token | Out-String))) {
        throw "Workbench runner token unavailable from container '$ContainerName'. Start Companion first."
    }
    return (($token | Out-String).Trim())
}

$script:RunnerToken = Get-RunnerToken
$script:Headers = @{
    'X-Workbench-Runner-Token' = $script:RunnerToken
    'X-Workbench-Runner-Id' = $RunnerId
}

function Invoke-WorkbenchApi {
    param(
        [Parameter(Mandatory)][ValidateSet('Get','Post')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [int]$TimeoutSec = 20
    )
    $params = @{
        Uri = "$CompanionUrl$Path"
        Method = $Method
        Headers = $script:Headers
        TimeoutSec = $TimeoutSec
        ErrorAction = 'Stop'
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = $Body | ConvertTo-Json -Depth 16 -Compress
    }
    Invoke-RestMethod @params
}

$script:RemoteProbeIntervalSeconds = 30
$script:RemoteProbeCache = [ordered]@{
    observed_utc = [DateTimeOffset]::MinValue
    gateway_state = 'waiting_dependency'
    am4_state = 'waiting_dependency'
    i5_state = 'waiting_dependency'
}

function Send-Heartbeat {
    $dockerReady = $false
    try {
        $null = docker version --format '{{.Server.Version}}' 2>$null
        $dockerReady = ($LASTEXITCODE -eq 0)
    } catch { $dockerReady = $false }

    $omen = if (Get-Process -Name valheim -ErrorAction SilentlyContinue) { 'working' } else { 'ready' }
    $now = [DateTimeOffset]::UtcNow
    if (($now - $script:RemoteProbeCache.observed_utc).TotalSeconds -ge $script:RemoteProbeIntervalSeconds) {
        $am4 = 'offline'
        $i5 = 'offline'
        $gateway = 'offline'
        try {
            $gatewayProbe = Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/release/check" -TimeoutSec 4
            $gateway = if ($gatewayProbe.error) { 'offline' } else { 'ready' }
        } catch { $gateway = 'offline' }
        try {
            $am4Probe = & ssh -o BatchMode=yes -o ConnectTimeout=3 am4 "docker inspect --format '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}' comfy-valheim-server-am4-valheim-server-1" 2>$null
            $am4Text = (($am4Probe | Out-String).Trim())
            if ($LASTEXITCODE -eq 0 -and $am4Text -match '^running\|(healthy)?$') { $am4 = 'ready' }
            elseif ($LASTEXITCODE -eq 0 -and $am4Text -match '^running\|') { $am4 = 'working' }
        } catch { $am4 = 'offline' }
        try {
            $i5Probe = & ssh -o BatchMode=yes -o ConnectTimeout=3 i5 "echo ready" 2>$null
            if ($LASTEXITCODE -eq 0 -and $i5Probe) { $i5 = 'ready' }
        } catch { $i5 = 'offline' }
        $script:RemoteProbeCache.gateway_state = $gateway
        $script:RemoteProbeCache.am4_state = $am4
        $script:RemoteProbeCache.i5_state = $i5
        $script:RemoteProbeCache.observed_utc = $now
    }

    $heartbeat = [ordered]@{
        schema_version = 1
        observed_utc = $now.ToString('O')
        runner_version = 'workbench-runner-v1'
        gateway_state = $script:RemoteProbeCache.gateway_state
        am4_state = $script:RemoteProbeCache.am4_state
        omen_state = $omen
        i5_state = $script:RemoteProbeCache.i5_state
        docker_ready = $dockerReady
        source_revision = (& git -C $RepoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    }
    try { Invoke-WorkbenchApi -Method Post -Path '/api/v1/workbench/runner/heartbeat' -Body $heartbeat | Out-Null } catch { }
}

function Send-Event {
    param([string]$JobId, [string]$State, [string]$Reason)
    Invoke-WorkbenchApi -Method Post -Path ("/api/v1/workbench/runner/jobs/{0}/events" -f [Uri]::EscapeDataString($JobId)) -Body @{ state = $State; reason_code = $Reason } | Out-Null
}

function Complete-Job {
    param([string]$JobId, [ValidateSet('passed','failed')][string]$Verdict, [object]$Result, [string]$ReasonCode)
    Invoke-WorkbenchApi -Method Post -Path ("/api/v1/workbench/runner/jobs/{0}/complete" -f [Uri]::EscapeDataString($JobId)) -Body @{ verdict = $Verdict; result = $Result; reason_code = $ReasonCode } -TimeoutSec 30 | Out-Null
}

function Send-Artifact {
    param([string]$JobId, [string]$Name, [string]$Sha256, [long]$SizeBytes, [string]$PrivacyClass = 'private_local')
    Invoke-WorkbenchApi -Method Post -Path ("/api/v1/workbench/runner/jobs/{0}/artifacts" -f [Uri]::EscapeDataString($JobId)) -Body @{ name = $Name; sha256 = $Sha256; size_bytes = $SizeBytes; privacy_class = $PrivacyClass } -TimeoutSec 30 | Out-Null
}

function Wait-ForHuman {
    param([string]$JobId, [string]$Reason = 'human_observation_required', [object]$Result = $null)
    Invoke-WorkbenchApi -Method Post -Path ("/api/v1/workbench/runner/jobs/{0}/waiting-human" -f [Uri]::EscapeDataString($JobId)) -Body @{ reason_code = $Reason; result = $Result } -TimeoutSec 30 | Out-Null
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-RunnerChildProcess {
    param(
        [Parameter(Mandatory)][string]$JobId,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    # Start-Process returns a Process object reopened by PID on Windows
    # PowerShell 5.1. That object has no originating process handle, so ExitCode
    # remains null even after WaitForExit. Start the Process object directly.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $ArgumentList -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "failed to start child process '$FilePath'." }
    $exitCode = $null
    try {
        while (-not $process.WaitForExit(20000)) {
            Send-Heartbeat
            Send-Event $JobId 'running' 'runner_dispatch_active'
        }
        $process.WaitForExit()
        # Capture before Dispose(): Process.ExitCode becomes unavailable after
        # disposal, and a return expression inside try is evaluated after finally.
        $exitCode = $process.ExitCode
    } finally {
        $process.Dispose()
    }
    return $exitCode
}

function Invoke-DockerModBuild {
    param([string]$JobId)
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj'))) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.mod.release'; required = 'source_checkout' }; reason = 'source_checkout_required' }
    }
    if (-not (Test-Path -LiteralPath $ValheimPath)) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.mod.release'; required = 'valheim_install'; path = $ValheimPath }; reason = 'valheim_install_missing' }
    }
    $assemblyPath = Join-Path $ValheimPath 'valheim_Data\Managed\assembly_valheim.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.mod.release'; required = 'assembly_valheim.dll'; path = $assemblyPath }; reason = 'valheim_assembly_missing' }
    }
    $dll = Join-Path $RepoRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
    $args = @(
        'run', '--rm',
        '--mount', "type=bind,source=$RepoRoot,destination=/src",
        '--mount', "type=bind,source=$ValheimPath,destination=/valheim,readonly",
        '--workdir', '/src/network/mod/ComfyNetworkSense',
        'mcr.microsoft.com/dotnet/sdk:9.0',
        'dotnet', 'build', 'ComfyNetworkSense.csproj', '-c', 'Release',
        '-p:ValheimDir=/valheim', '-p:PluginOutputPath=/tmp/no-plugin-copy', '-p:ComfyCopyToPlugins=false'
    )
    $output = @(& docker @args 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.mod.release'; output_tail = @($output | Select-Object -Last 80) }; reason = 'container_build_failed' }
    }
    if (-not (Test-Path -LiteralPath $dll)) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.mod.release'; output_tail = @($output | Select-Object -Last 80) }; reason = 'build_artifact_missing' }
    }
    $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    return @{ verdict = 'passed'; result = @{ capability = 'build.mod.release'; artifact_name = 'ComfyNetworkSense.dll'; artifact_sha256 = $hash; plugin_copy = $false; valheim_mount = 'readonly'; host_sdk_required = $false; output_tail = @($output | Select-Object -Last 30) }; reason = 'container_build_verified' }
}

function Invoke-CompanionCapture {
    param([string]$JobId, $Inputs)
    try {
        $duration = 15
        if ($Inputs -and $Inputs.PSObject.Properties['duration_seconds']) { $duration = [Math]::Min(180, [Math]::Max(5, [int]$Inputs.duration_seconds)) }
        $capture = Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/transport-capture" -Method Post -Headers @{ 'Content-Type' = 'application/json' } -Body (@{ duration_seconds = $duration; interval_seconds = 1; label = "workbench-$JobId" } | ConvertTo-Json) -TimeoutSec ([Math]::Max(30, $duration + 30))
        return @{ verdict = 'passed'; result = @{ capability = 'operate.transport.capture'; run_id = $capture.run_id; verdict = $capture.verdict }; reason = 'transport_capture_complete' }
    } catch {
        return @{ verdict = 'failed'; result = @{ capability = 'operate.transport.capture'; error = $_.Exception.Message }; reason = 'transport_capture_failed' }
    }
}

function Invoke-ModOperation {
    param([string]$CapabilityId, $Inputs)
    try {
        if ($CapabilityId -eq 'operate.mod.check') {
            $manifest = Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/update/check" -Method Get -TimeoutSec 30
            return @{ verdict = 'passed'; result = @{ capability = $CapabilityId; manifest = $manifest }; reason = 'mod_manifest_read' }
        }
        $confirmation = @{ game_closed_confirmed = $true } | ConvertTo-Json
        if ($CapabilityId -eq 'operate.mod.install') {
            $result = Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/update/install" -Method Post -Headers @{ 'Content-Type' = 'application/json' } -Body $confirmation -TimeoutSec 300
            return @{ verdict = if ($result.ok) { 'passed' } else { 'failed' }; result = @{ capability = $CapabilityId; updater = $result }; reason = if ($result.ok) { 'mod_install_complete' } else { 'mod_install_failed' } }
        }
        if ($CapabilityId -eq 'operate.mod.rollback') {
            $result = Invoke-RestMethod -Uri "$CompanionUrl/api/v0/companion/update/rollback" -Method Post -Headers @{ 'Content-Type' = 'application/json' } -Body $confirmation -TimeoutSec 60
            return @{ verdict = if ($result.ok) { 'passed' } else { 'failed' }; result = @{ capability = $CapabilityId; updater = $result }; reason = if ($result.ok) { 'mod_rollback_complete' } else { 'mod_rollback_failed' } }
        }
        return @{ verdict = 'failed'; result = @{ capability = $CapabilityId }; reason = 'capability_handler_not_implemented' }
    } catch {
        $reason = if ($CapabilityId -eq 'operate.mod.check') { 'gateway_manifest_unavailable' } else { 'mod_operation_failed' }
        return @{ verdict = 'failed'; result = @{ capability = $CapabilityId; error_class = $reason; error = $_.Exception.Message }; reason = $reason }
    }
}

function Invoke-SupportExport {
    param([string]$JobId)
    $exportRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Lumberjacks\Workbench\exports'
    New-Item -ItemType Directory -Force -Path $exportRoot | Out-Null
    $projection = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench" -Method Get -TimeoutSec 20
    $safe = [ordered]@{
        schema_version = 1
        export_type = 'public_safe_workbench_support'
        generated_utc = [DateTimeOffset]::UtcNow
        profile = $projection.profile.effective
        source = $projection.source
        topology = $projection.topology
        jobs = @($projection.jobs | ForEach-Object {
            [ordered]@{ job_id = $_.job_id; capability_id = $_.capability_id; state = $_.state; verdict = $_.verdict; reason_code = $_.reason_code; created_utc = $_.created_utc; updated_utc = $_.updated_utc }
        })
        privacy = 'no_player_names_ids_coordinates_free_text_secrets_or_raw_responses'
    }
    $name = "support-$JobId.json"
    $path = Join-Path $exportRoot $name
    [IO.File]::WriteAllText($path, ($safe | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    $privacyGate = Join-Path $RepoRoot 'tools\workbench\Test-WorkbenchSupportExport.ps1'
    if (-not (Test-Path -LiteralPath $privacyGate)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        throw 'support_export_privacy_gate_missing'
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $privacyGate -Path $path | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        throw 'support_export_privacy_gate_failed'
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    return @{ verdict = 'passed'; result = @{ capability = 'recover.support.export'; artifact_name = $name; artifact_sha256 = $hash; scope = 'public_safe' }; reason = 'public_safe_support_export_complete' }
}

function Invoke-RecreateVerify {
    param([string]$JobId)
    $before = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/installation" -Method Get -TimeoutSec 20
    if (-not (Test-Path -LiteralPath $ComposeFile)) { throw 'compose_file_missing' }
    $composeResolved = (Resolve-Path -LiteralPath $ComposeFile).Path
    if ([IO.Path]::GetFullPath($composeResolved) -ne $ComposeFile) { throw 'compose_file_identity_changed' }
    $config = & docker compose -p $ProjectName -f $ComposeFile config --format json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($config | Out-String))) { throw 'compose_config_unreadable' }
    $configText = ($config | Out-String)
    if ($configText -notmatch 'companion-data') { throw 'compose_state_volume_not_declared' }
    $profile = $env:LUMBERJACKS_WORKBENCH_PROFILE
    $allowed = @($ContainerName)
    if ($profile -in @('Dev', 'Lab')) { $allowed += @("$ProjectName-dev-mcp-1", "$ProjectName-workbench-tool-runner-1") }
    $running = @(docker ps --filter "label=com.docker.compose.project=$ProjectName" --format '{{.Names}}')
    $unexpected = @($running | Where-Object { $_ -notin $allowed })
    if ($unexpected.Count -gt 0) {
        Write-RunnerDiagnostic ("recreate rejected profile={0}; allowed={1}; running={2}; unexpected={3}" -f $Profile, ($allowed -join ','), ($running -join ','), ($unexpected -join ','))
        throw 'compose_project_has_unexpected_containers'
    }
    $profileArgs = if ($profile -in @('Dev', 'Lab')) { @('--profile', $profile.ToLowerInvariant()) } else { @() }
    $projectDir = Split-Path -Parent $ComposeFile
    Push-Location $projectDir
    try {
        docker compose -p $ProjectName -f $ComposeFile @profileArgs down
        if ($LASTEXITCODE -ne 0) { throw 'compose_down_failed' }
        docker compose -p $ProjectName -f $ComposeFile @profileArgs up -d --build
        if ($LASTEXITCODE -ne 0) { throw 'compose_up_failed' }
    } finally { Pop-Location }
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        try { $health = Invoke-RestMethod "$CompanionUrl/health" -TimeoutSec 3; if ($health.ok) { break } } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $health.ok) { throw 'companion_did_not_return_after_recreate' }
    $after = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench/installation" -Method Get -TimeoutSec 20
    $same = $before.installation_id -eq $after.installation_id -and $before.claimed -eq $after.claimed
    if (-not $same) { throw 'installation_identity_changed_during_recreate' }
    return @{ verdict = 'passed'; result = @{ capability = 'recover.recreate.verify'; installation_id = $after.installation_id; claimed = $after.claimed; volume_preserved = $true }; reason = 'recreate_preserved_owned_state' }
}

function Invoke-RenderedC6 {
    param([string]$JobId)
    $projection = Invoke-RestMethod -Uri "$CompanionUrl/api/v1/workbench" -Method Get -TimeoutSec 20
    $source = $projection.source
    if ([string]::IsNullOrWhiteSpace([string]$source.source_revision) -or [string]$source.source_revision -eq 'unknown') {
        return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; source = $source }; reason = 'rendered_prelive_source_identity_missing' }
    }
    if ([string]$source.source_dirty -ne 'false') {
        return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; source = $source }; reason = 'rendered_prelive_source_dirty' }
    }
    if ([string]::IsNullOrWhiteSpace([string]$source.image) -or [string]$source.image -eq 'unknown') {
        return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; source = $source }; reason = 'rendered_prelive_image_identity_missing' }
    }
    # Do not pipe Docker into Select-Object -First: closing that native pipeline
    # after the first line makes a successful Docker CLI report exit -1 on
    # Windows. The formatted inspect emits exactly one line for one image.
    $imageId = & docker image inspect ([string]$source.image) --format '{{.Id}}' 2>$null
    $imageInspectExit = $LASTEXITCODE
    if ($imageInspectExit -ne 0 -or [string]::IsNullOrWhiteSpace([string]$imageId)) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; image = $source.image }; reason = 'rendered_prelive_image_missing' }
    }
    $requiredNodes = @('runner', 'docker', 'am4', 'i5')
    $nodeStates = @{}
    foreach ($node in $projection.topology.nodes) { $nodeStates[$node.id] = $node.state }
    $missing = @($requiredNodes | Where-Object { $nodeStates[$_] -ne 'ready' })
    if ($missing.Count -gt 0) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; missing_nodes = $missing; node_states = $nodeStates }; reason = 'rendered_prelive_dependency_missing' }
    }
    $i5Link = Join-Path $RepoRoot 'tools\i5\Test-I5Link.ps1'
    if (-not (Test-Path -LiteralPath $i5Link)) { return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; required = 'source_checkout' }; reason = 'source_checkout_required' } }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i5Link | Out-Null
    if ($LASTEXITCODE -ne 0) { return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; i5 = 'offline_or_preflight_failed' }; reason = 'rendered_prelive_i5_failed' } }
    $dllPath = Join-Path $RepoRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
    if (-not (Test-Path -LiteralPath $dllPath)) { return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; required = 'built_mod_artifact' }; reason = 'rendered_prelive_dll_missing' } }
    $dllHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runId = "workbench-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))-$($JobId.Substring($JobId.Length - 8))"
    $runRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Lumberjacks\Workbench\runs'
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $scenario = Join-Path $runRoot "$runId.json"
    $evidence = Join-Path $runRoot $runId
    $generator = Join-Path $RepoRoot 'fieldlab\scripts\New-NativeValheimCutoverScenario.ps1'
    $orchestrator = Join-Path $RepoRoot 'fieldlab\scripts\Invoke-NativeValheimCutoverScenario.ps1'
    if (-not (Test-Path -LiteralPath $generator) -or -not (Test-Path -LiteralPath $orchestrator)) {
        return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; required = 'source_checkout' }; reason = 'source_checkout_required' }
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $generator -RunId $runId -OutputPath $scenario -Profile c6 -MotionDurationSeconds 6
    if ($LASTEXITCODE -ne 0) { return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; run_id = $runId }; reason = 'scenario_generation_failed' } }
    $orchestratorExit = Invoke-RunnerChildProcess -JobId $JobId -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-ProcessArgument $orchestrator),
        '-RunId', (Quote-ProcessArgument $runId),
        '-ScenarioPath', (Quote-ProcessArgument $scenario),
        '-EvidenceRoot', (Quote-ProcessArgument $evidence),
        '-EnableMotionAuthorityCutover', '-WaitSeconds', '900'
    )
    if ($orchestratorExit -ne 0) { return @{ verdict = 'failed'; result = @{ capability = 'build.rendered.c6-role-reversal'; run_id = $runId; evidence_root = $evidence }; reason = 'rendered_role_reversal_failed' } }
    return @{ state = 'waiting_human'; result = @{ capability = 'build.rendered.c6-role-reversal'; run_id = $runId; evidence_root_name = Split-Path -Leaf $evidence; dll_sha256 = $dllHash; prelive = @{ required_nodes = $requiredNodes; i5 = 'passed'; source_revision = $source.source_revision; image = $source.image; image_id = ([string]$imageId).Trim() }; human_observation = 'required_after_machine_run' }; reason = 'rendered_role_reversal_complete' }
}

function Invoke-Job {
    param($Job)
    Send-Event $Job.job_id 'running' 'runner_dispatch_started'
    try {
        $outcome = switch ($Job.capability_id) {
            'build.mod.release' { Invoke-DockerModBuild $Job.job_id }
            'operate.transport.capture' { Invoke-CompanionCapture $Job.job_id $Job.inputs }
            'operate.mod.check' { Invoke-ModOperation $Job.capability_id $Job.inputs }
            'operate.mod.install' { Invoke-ModOperation $Job.capability_id $Job.inputs }
            'operate.mod.rollback' { Invoke-ModOperation $Job.capability_id $Job.inputs }
            'recover.support.export' { Invoke-SupportExport $Job.job_id }
            'recover.recreate.verify' { Invoke-RecreateVerify $Job.job_id }
            'build.rendered.c6-role-reversal' { Invoke-RenderedC6 $Job.job_id }
            default { @{ verdict = 'failed'; result = @{ capability = $Job.capability_id }; reason = 'capability_handler_not_implemented' } }
        }
        if ($outcome.result -and $outcome.result.artifact_name -and $outcome.result.artifact_sha256) {
            $artifactPath = if ($outcome.result.artifact_name -eq 'ComfyNetworkSense.dll') { Join-Path $RepoRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll' } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Lumberjacks\Workbench\exports\' + $outcome.result.artifact_name) }
            $size = if (Test-Path -LiteralPath $artifactPath) { (Get-Item -LiteralPath $artifactPath).Length } else { 0 }
            Send-Artifact $Job.job_id $outcome.result.artifact_name $outcome.result.artifact_sha256 $size $(if ($Job.capability_id -eq 'recover.support.export') { 'public_safe' } else { 'private_local' })
        }
        if ($outcome.state -eq 'waiting_human') {
            Wait-ForHuman $Job.job_id $outcome.reason $outcome.result
        } else {
            Complete-Job $Job.job_id $outcome.verdict $outcome.result $outcome.reason
        }
    } catch {
        Write-RunnerDiagnostic ("job {0} failed: {1}" -f $Job.job_id, $_.Exception.Message)
        try { Complete-Job $Job.job_id 'failed' @{ capability = $Job.capability_id; error = $_.Exception.Message } 'runner_exception' } catch { }
    }
}

do {
    Send-Heartbeat
    try {
        $next = Invoke-WorkbenchApi -Method Get -Path '/api/v1/workbench/runner/jobs/next'
        if ($next.job) { Invoke-Job $next.job }
    } catch { }
    if (-not $Once) { Start-Sleep -Seconds $PollSeconds }
} while (-not $Once)
