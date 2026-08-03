#Requires -Version 5.1
<#
.SYNOPSIS
Capture the preserved P7 boot incident, install the staged boot fix, and prove it.

.DESCRIPTION
Preflight is read-only. Run mode requires -Execute, starts a TERMINATED P7 once,
captures the still-preserved incident state before changing the unit, refuses to
continue if the world save is hazardous or Valheim already started on that boot,
installs the boot fix transactionally, performs a real stop/start, and injects a
one-shot systemd failure to prove automatic retry. A passed receipt is bound to
the GCE instance id and current Linux boot id so it cannot be reused after a
different boot.
#>
[CmdletBinding()]
param(
    [ValidateSet('preflight', 'run')]
    [string] $Action = 'preflight',

    [switch] $Execute,

    [string] $RunId = '',

    [string] $OutputPath = '',

    [ValidateRange(120, 900)]
    [int] $SshWaitSeconds = 420,

    [ValidateRange(300, 1800)]
    [int] $StackWaitSeconds = 1200
)

$ErrorActionPreference = 'Stop'
$project = 'lumberjacks-exp-20260711-djc'
$zone = 'us-west1-b'
$instance = 'comfy-lumberjacks-p7'
$sshTarget = 'comfy-p7'
$service = 'comfy-lumberjacks-p7.service'
$composeProject = 'comfy-lumberjacks-p7'
$postgresContainer = 'comfy-lumberjacks-p7-postgres-1'
$valheimContainer = 'comfy-lumberjacks-p7-valheim-server-1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$unitPath = Join-Path $repoRoot 'infra\gcp\p7\comfy-lumberjacks-p7.service'
$unitSha256 =
    (Get-FileHash -LiteralPath $unitPath -Algorithm SHA256).Hash.ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = 'p7-boot-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
}
if ($RunId.Length -gt 80 -or $RunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw "RunId must be an 80-character-or-shorter safe token: $RunId"
}
if ($Action -eq 'run' -and -not $Execute) {
    throw '-Action run requires -Execute because it changes P7 power and system state.'
}
if ($Action -eq 'preflight' -and $Execute) {
    throw '-Execute is valid only with -Action run.'
}
if ($Action -eq 'run' -and [string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot `
        "fieldlab\runs\p7-boot-determinism\$RunId\acceptance.json"
}
if ($Action -eq 'run') {
    # The first remote capture intentionally happens before any boot fix is
    # installed. Create the local receipt directory before that capture so a
    # failed evidence write cannot strand P7 in its guarded running state.
    $outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if ($outputDirectory) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
}

function Write-Receipt([object] $Receipt) {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $absolute = [IO.Path]::GetFullPath($OutputPath)
        $directory = Split-Path -Parent $absolute
        if ($directory) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        [IO.File]::WriteAllText(
            $absolute,
            ($Receipt | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    }
    $Receipt | ConvertTo-Json -Depth 12
}

function Write-EvidenceText([string] $Name, [string] $Text) {
    $outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    $path = Join-Path $outputDirectory $Name
    [IO.File]::WriteAllText(
        $path,
        $Text + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    return $path
}

function Invoke-NativeJob(
    [string] $Exe,
    [string[]] $Arguments,
    [int] $TimeoutSeconds) {
    $serializedArguments = ConvertTo-Json -InputObject @($Arguments) -Compress
    $job = Start-Job -ScriptBlock {
        param($program, $programArgumentsJson)
        $programArguments = [object[]](ConvertFrom-Json $programArgumentsJson)
        $output = & $program @programArguments 2>&1
        [pscustomobject]@{
            Output = (@($output) -join [Environment]::NewLine)
            Code = $LASTEXITCODE
        }
    } -ArgumentList @($Exe, $serializedArguments)
    $done = Wait-Job $job -Timeout $TimeoutSeconds
    if (-not $done) {
        Stop-Job $job
        Remove-Job $job -Force
        return $null
    }
    $result = Receive-Job $job
    Remove-Job $job -Force
    return $result
}

function Invoke-CheckedNative(
    [string] $Exe,
    [string[]] $Arguments,
    [string] $Label,
    [int] $TimeoutSeconds) {
    $result = Invoke-NativeJob $Exe $Arguments $TimeoutSeconds
    if ($null -eq $result) { throw "$Label timed out." }
    if ($result.Code -ne 0) {
        throw "$Label failed with exit $($result.Code): $($result.Output)"
    }
    return [string]$result.Output
}

function Invoke-Remote(
    [string] $Script,
    [string] $Label,
    [int] $TimeoutSeconds = 300) {
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n")))
    return Invoke-CheckedNative `
        -Exe 'ssh' `
        -Arguments @(
            '-n',
            '-o', 'BatchMode=yes',
            '-o', 'ConnectTimeout=15',
            '-o', 'ServerAliveInterval=15',
            '-o', 'ServerAliveCountMax=4',
            $sshTarget,
            "echo $encoded | base64 -d | bash") `
        -Label $Label `
        -TimeoutSeconds $TimeoutSeconds
}

function Get-P7Vm {
    $json = Invoke-CheckedNative `
        -Exe 'gcloud' `
        -Arguments @(
            'compute', 'instances', 'describe', $instance,
            '--zone', $zone,
            '--project', $project,
            '--format=json') `
        -Label 'P7 instance-state lookup' `
        -TimeoutSeconds 60
    return $json | ConvertFrom-Json
}

function Set-P7Power([ValidateSet('start', 'stop')][string] $Operation) {
    [void](Invoke-CheckedNative `
        -Exe 'gcloud' `
        -Arguments @(
            'compute', 'instances', $Operation, $instance,
            '--zone', $zone,
            '--project', $project,
            '--quiet') `
        -Label "P7 $Operation" `
        -TimeoutSeconds 600)
}

function Wait-P7Ssh {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($SshWaitSeconds)
    $nextReport = [DateTimeOffset]::UtcNow
    do {
        $probe = Invoke-NativeJob `
            -Exe 'ssh' `
            -Arguments @(
                '-n', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8',
                $sshTarget, 'true') `
            -TimeoutSeconds 15
        if ($probe -and $probe.Code -eq 0) { return }
        if ([DateTimeOffset]::UtcNow -ge $nextReport) {
            Write-Host '[p7-boot] waiting for BatchMode SSH...'
            $nextReport = [DateTimeOffset]::UtcNow.AddSeconds(30)
        }
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "P7 SSH did not become ready within $SshWaitSeconds seconds."
}

function Read-KeyValues([string] $Text) {
    $values = @{}
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '^([a-z0-9_]+)=(.*)$') {
            $values[$matches[1]] = $matches[2]
        }
    }
    return $values
}

function Get-StackState([string] $SinceUtc) {
    $script = @"
set -u
since='$SinceUtc'
rows="`$(sudo docker ps -a --filter 'label=com.docker.compose.project=$composeProject' --format '{{.Names}}|{{.State}}|{{.Status}}')"
total="`$(printf '%s\n' "`$rows" | sed '/^`$/d' | wc -l | tr -d ' ')"
running="`$(printf '%s\n' "`$rows" | awk -F'|' '`$2 == "running" { count++ } END { print count+0 }')"
created="`$(printf '%s\n' "`$rows" | awk -F'|' '`$2 == "created" { count++ } END { print count+0 }')"
unit="`$(systemctl is-active '$service' 2>/dev/null || true)"
enabled="`$(systemctl is-enabled '$service' 2>/dev/null || true)"
postgres_health="`$(sudo docker inspect '$postgresContainer' --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' 2>/dev/null || true)"
gateway_health=false
if curl --fail --silent http://127.0.0.1:4000/health | grep -q '"status":"ok"'; then gateway_health=true; fi
server_ready=false
if sudo docker logs --since "`$since" '$valheimContainer' 2>&1 | grep -q 'Game server connected'; then server_ready=true; fi
printf 'boot_id=%s\n' "`$(cat /proc/sys/kernel/random/boot_id)"
printf 'unit_active=%s\n' "`$unit"
printf 'unit_enabled=%s\n' "`$enabled"
printf 'container_total=%s\n' "`$total"
printf 'container_running=%s\n' "`$running"
printf 'container_created=%s\n' "`$created"
printf 'postgres_health=%s\n' "`$postgres_health"
printf 'gateway_health=%s\n' "`$gateway_health"
printf 'server_ready=%s\n' "`$server_ready"
printf 'restart_count=%s\n' "`$(systemctl show '$service' -p NRestarts --value 2>/dev/null || printf 0)"
"@
    return Read-KeyValues (Invoke-Remote $script 'P7 stack-state probe' 90)
}

function Test-StackGreen([hashtable] $State) {
    return (
        [string]$State.unit_active -eq 'active' -and
        [string]$State.unit_enabled -eq 'enabled' -and
        [int]$State.container_total -eq 7 -and
        [int]$State.container_running -eq 7 -and
        [int]$State.container_created -eq 0 -and
        [string]$State.postgres_health -eq 'healthy' -and
        [string]$State.gateway_health -eq 'true' -and
        [string]$State.server_ready -eq 'true')
}

function Wait-StackGreen([string] $SinceUtc) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StackWaitSeconds)
    $nextReport = [DateTimeOffset]::UtcNow
    $last = $null
    do {
        try {
            $last = Get-StackState $SinceUtc
            if (Test-StackGreen $last) { return $last }
        } catch { }
        if ([DateTimeOffset]::UtcNow -ge $nextReport) {
            $detail = if ($last) {
                "unit=$($last.unit_active) containers=$($last.container_running)/$($last.container_total) " +
                    "created=$($last.container_created) postgres=$($last.postgres_health) " +
                    "gateway=$($last.gateway_health) valheim=$($last.server_ready)"
            } else { 'state probe unavailable' }
            Write-Host "[p7-boot] waiting for stack convergence: $detail"
            $nextReport = [DateTimeOffset]::UtcNow.AddSeconds(30)
        }
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $detail = if ($last) { $last | ConvertTo-Json -Compress } else { 'unreachable' }
    throw "P7 stack did not converge within $StackWaitSeconds seconds: $detail"
}

function Wait-PublicHealth {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        try {
            $response = Invoke-WebRequest `
                -UseBasicParsing `
                -Uri 'https://comfy-p7.duckdns.org/health' `
                -TimeoutSec 10
            if ([int]$response.StatusCode -eq 200) { return 200 }
        } catch { }
        Start-Sleep -Seconds 3
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return 0
}

$initialVm = Get-P7Vm
$preflight = [ordered]@{
    schema_version = 1
    receipt_type = 'p7_boot_determinism_preflight'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    action = $Action
    execute = [bool]$Execute
    project = $project
    zone = $zone
    instance = $instance
    instance_id = [string]$initialVm.id
    initial_status = [string]$initialVm.status
    unit_path = $unitPath
    unit_sha256 = $unitSha256
    ready = [string]$initialVm.status -eq 'TERMINATED'
    actions = @(
        'start the terminated VM and capture preserved pre-fix evidence',
        'refuse if Valheim already started, the save is hazardous, disk is unsafe, or init.sql is missing',
        'transactionally install and enable the unit plus docker mount-order drop-in',
        'perform a real GCE stop/start and prove all seven services, TLS, and Valheim readiness',
        'inject one first-start systemd failure and prove automatic retry recovery',
        'emit an instance-id and boot-id bound acceptance receipt')
    result = if ([string]$initialVm.status -eq 'TERMINATED') {
        'ready'
    } else {
        'not_ready'
    }
}
if ($Action -eq 'preflight') {
    Write-Receipt $preflight
    if (-not $preflight.ready) { exit 3 }
    return
}
if (-not $preflight.ready) {
    throw "Boot proof requires P7 TERMINATED at entry; actual=$($initialVm.status)."
}

$stage = 'start_for_preserved_capture'
$acceptance = $null
$retryDropInInstalled = $false
try {
    Write-Host '[p7-boot] starting P7 for the preserved incident capture...'
    Set-P7Power start
    Wait-P7Ssh

    $stage = 'capture_preserved_incident'
    # This is deliberately the first SSH action. Capture the save/container
    # state, then runtime-mask the old unit and stop Valheim before the longer
    # journal/disk collection. That closes the race where a newly successful
    # old boot could load the world while evidence was still being gathered.
    $guardScript = @"
set -u
boot_epoch="`$(awk '/^btime / { print `$2 }' /proc/stat)"
db='/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.db'
db_new='/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.db.new'
valheim_state="`$(sudo docker inspect '$valheimContainer' --format '{{.State.Status}}' 2>/dev/null || true)"
server_ready=false
world_load_started=false
if sudo docker logs --since "`$boot_epoch" '$valheimContainer' 2>&1 | grep -q 'Game server connected'; then server_ready=true; fi
if sudo docker logs --since "`$boot_epoch" '$valheimContainer' 2>&1 | grep -Eq 'Loading world|World loaded|ZDOS:'; then world_load_started=true; fi
echo '=== immediate pre-guard containers ==='
sudo docker ps -a --format 'table {{.Names}}\t{{.Status}}\t{{.RunningFor}}'
printf 'boot_id=%s\n' "`$(cat /proc/sys/kernel/random/boot_id)"
printf 'world_db_exists=%s\n' "`$(sudo test -s "`$db" && printf true || printf false)"
printf 'world_db_mtime=%s\n' "`$(sudo stat -c %Y "`$db" 2>/dev/null || printf 0)"
printf 'world_new_exists=%s\n' "`$(sudo test -e "`$db_new" && printf true || printf false)"
printf 'world_new_mtime=%s\n' "`$(sudo stat -c %Y "`$db_new" 2>/dev/null || printf 0)"
printf 'valheim_state_before_guard=%s\n' "`$valheim_state"
printf 'server_ready_current_boot=%s\n' "`$server_ready"
printf 'world_load_started_current_boot=%s\n' "`$world_load_started"
sudo systemctl mask --runtime '$service' >/dev/null 2>&1 || true
sudo docker stop --time 10 '$valheimContainer' >/dev/null 2>&1 || true
sudo systemctl stop --no-block '$service' >/dev/null 2>&1 || true
sleep 2
guard_state="`$(systemctl is-enabled '$service' 2>/dev/null || true)"
guard_link=false
if test -L "/run/systemd/system/$service" &&
   test "`$(readlink "/run/systemd/system/$service")" = '/dev/null'; then
  guard_link=true
fi
printf 'guard_applied=%s\n' "`$guard_link"
"@
    Write-Host '[p7-boot] capturing and guarding the frozen pre-fix state...'
    $guardText = Invoke-Remote $guardScript 'immediate pre-fix capture guard' 120
    $guardState = Read-KeyValues $guardText

    $captureScript = @"
set +e
echo '=== identity ==='
date -u --iso-8601=seconds
cat /proc/sys/kernel/random/boot_id
sudo journalctl --list-boots --no-pager
echo '=== world files ==='
sudo ls -la --time-style=long-iso /mnt/comfy-p7/valheim/config/worlds_local/ | grep -i comfyera16
sudo stat -c '%n|%s|%Y' /mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.*
echo '=== disk ==='
df -h / /mnt/comfy-p7
sudo du -sh /opt/lumberjacks-* /var/lib/docker 2>/dev/null
echo '=== previous boot unit ==='
sudo journalctl -u '$service' -b -1 --no-pager | tail -120
echo '=== current boot unit ==='
sudo journalctl -u '$service' -b --no-pager | tail -120
echo '=== containers before fix ==='
sudo docker ps -a --format 'table {{.Names}}\t{{.Status}}\t{{.RunningFor}}'
sudo docker inspect '$postgresContainer' --format '{{.State.Status}} exit={{.State.ExitCode}} err={{.State.Error}} oom={{.State.OOMKilled}} restart={{.HostConfig.RestartPolicy.Name}}'
echo '=== mount ordering ==='
findmnt /mnt/comfy-p7
sudo journalctl -u docker -b --no-pager | head -40
sudo systemctl status 'mnt-comfy\x2dp7.mount' --no-pager
echo '=== unit and guarded paths ==='
sudo systemctl is-enabled '$service'
sudo systemctl status '$service' --no-pager
sudo ls -l /etc/systemd/system/$service /opt/comfy/infra/gcp/p7/docker-compose.yml /etc/comfy-p7/environment
echo '=== environment keys only ==='
sudo grep -o '^[A-Z_][A-Z0-9_]*=' /etc/comfy-p7/environment | sort
"@
    $captureText = Invoke-Remote $captureScript 'preserved incident capture' 300
    $capturePath = Write-EvidenceText `
        'pre-fix-evidence.txt' `
        ($guardText + [Environment]::NewLine + $captureText)

    $preStateScript = @"
set -u
db='/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.db'
db_new='/mnt/comfy-p7/valheim/config/worlds_local/ComfyEra16.db.new'
printf 'post_guard_world_new_exists=%s\n' "`$(sudo test -e "`$db_new" && printf true || printf false)"
printf 'post_guard_world_new_mtime=%s\n' "`$(sudo stat -c %Y "`$db_new" 2>/dev/null || printf 0)"
printf 'post_guard_valheim_state=%s\n' "`$(sudo docker inspect '$valheimContainer' --format '{{.State.Status}}' 2>/dev/null || true)"
printf 'root_available_bytes=%s\n' "`$(df --output=avail -B1 / | tail -1 | tr -d ' ')"
root="`$(sudo sed -n 's/^LUMBERJACKS_ROOT=//p' /etc/comfy-p7/environment | tail -1)"
printf 'init_sql_exists=%s\n' "`$(sudo test -s "`$root/infra/docker/init.sql" && printf true || printf false)"
printf 'compose_profile=%s\n' "`$(sudo sed -n 's/^COMPOSE_PROFILES=//p' /etc/comfy-p7/environment | tail -1)"
"@
    $postGuardState = Read-KeyValues (
        Invoke-Remote $preStateScript 'post-guard safety classification' 90)
    $preState = $guardState
    foreach ($entry in $postGuardState.GetEnumerator()) {
        $preState[$entry.Key] = $entry.Value
    }
    $worldHazard =
        [string]$preState.world_new_exists -eq 'true' -and
        [long]$preState.world_new_mtime -gt [long]$preState.world_db_mtime
    $postGuardWorldHazard =
        [string]$preState.post_guard_world_new_exists -eq 'true' -and
        [long]$preState.post_guard_world_new_mtime -gt
            [long]$preState.world_db_mtime
    if ([string]$preState.guard_applied -ne 'true') {
        throw 'The immediate runtime mask did not apply; fix application was refused.'
    }
    if ([string]$preState.world_db_exists -ne 'true') {
        throw 'The ComfyEra16.db save is missing or empty; fix application was refused.'
    }
    if ($worldHazard -or $postGuardWorldHazard) {
        throw 'ComfyEra16.db.new is newer than ComfyEra16.db; fix application was refused.'
    }
    if ([string]$preState.server_ready_current_boot -eq 'true' -or
        [string]$preState.world_load_started_current_boot -eq 'true') {
        throw 'Valheim began loading the world on the evidence-capture boot; it was stopped and fix application was refused to protect the save.'
    }
    if ([string]$preState.post_guard_valheim_state -eq 'running') {
        throw 'The immediate guard did not stop Valheim; fix application was refused.'
    }
    if ([long]$preState.root_available_bytes -lt 6GB) {
        throw 'P7 root disk has less than 6 GiB free; fix application was refused.'
    }
    if ([string]$preState.init_sql_exists -ne 'true') {
        throw 'The boot-critical Lumberjacks init.sql path is missing; fix application was refused.'
    }
    if ([string]$preState.compose_profile -notin @('', 'tls')) {
        throw "Unexpected COMPOSE_PROFILES value '$($preState.compose_profile)'; fix application was refused."
    }

    $stage = 'install_boot_fix'
    Write-Host '[p7-boot] safety gates passed; installing the boot fix transactionally...'
    $remoteUnit = "/tmp/$service.$RunId"
    [void](Invoke-CheckedNative `
        -Exe 'scp' `
        -Arguments @(
            '-o', 'BatchMode=yes',
            '-o', 'ServerAliveInterval=15',
            $unitPath,
            "${sshTarget}:$remoteUnit") `
        -Label 'P7 unit upload' `
        -TimeoutSeconds 180)
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $applyScript = @"
set -euo pipefail
unit='/etc/systemd/system/$service'
dropin='/etc/systemd/system/docker.service.d/10-comfy-p7-state-mount.conf'
env='/etc/comfy-p7/environment'
backup='/mnt/comfy-p7/backups/boot-determinism/$stamp'
remote_unit='$remoteUnit'
sudo install -d -m 0750 "`$backup"
unit_existed=false; dropin_existed=false
if sudo test -f "`$unit"; then sudo cp -a "`$unit" "`$backup/unit"; unit_existed=true; fi
if sudo test -f "`$dropin"; then sudo cp -a "`$dropin" "`$backup/docker-dropin"; dropin_existed=true; fi
sudo cp -a "`$env" "`$backup/environment"
enabled_before="`$(systemctl is-enabled '$service' 2>/dev/null || true)"
rollback() {
  status="`$?"
  if test "`$status" != 0; then
    if "`$unit_existed"; then sudo cp -a "`$backup/unit" "`$unit"; else sudo rm -f "`$unit"; fi
    if "`$dropin_existed"; then sudo install -d -m 0755 "`$(dirname "`$dropin")"; sudo cp -a "`$backup/docker-dropin" "`$dropin"; else sudo rm -f "`$dropin"; fi
    sudo cp -a "`$backup/environment" "`$env"
    sudo systemctl daemon-reload || true
    if test "`$enabled_before" = enabled; then sudo systemctl enable '$service' >/dev/null 2>&1 || true; else sudo systemctl disable '$service' >/dev/null 2>&1 || true; fi
    sudo systemctl mask --runtime '$service' >/dev/null 2>&1 || true
  fi
  sudo rm -f "`$remote_unit"
  exit "`$status"
}
trap rollback EXIT
sudo install -d -m 0755 "`$(dirname "`$dropin")"
printf '[Unit]\nRequiresMountsFor=/mnt/comfy-p7\n' | sudo tee "`$dropin" >/dev/null
sudo install -m 0644 "`$remote_unit" "`$unit"
profile="`$(sudo sed -n 's/^COMPOSE_PROFILES=//p' "`$env" | tail -1)"
if test -z "`$profile"; then printf '%s\n' 'COMPOSE_PROFILES=tls' | sudo tee -a "`$env" >/dev/null; elif test "`$profile" != tls; then exit 41; fi
root="`$(sudo sed -n 's/^LUMBERJACKS_ROOT=//p' "`$env" | tail -1)"
sudo test -s "`$root/infra/docker/init.sql"
sudo systemctl daemon-reload
sudo systemd-analyze verify "`$unit"
sudo systemctl unmask --runtime '$service' >/dev/null 2>&1 || true
sudo systemctl enable '$service' >/dev/null
test "`$(systemctl is-enabled '$service')" = enabled
test "`$(sudo grep -c '^COMPOSE_PROFILES=tls`$' "`$env")" = 1
printf 'backup_root=%s\n' "`$backup"
printf 'unit_sha256=%s\n' "`$(sudo sha256sum "`$unit" | awk '{print `$1}')"
printf 'enabled=%s\n' "`$(systemctl is-enabled '$service')"
trap - EXIT
sudo rm -f "`$remote_unit"
"@
    $applyState = Read-KeyValues (Invoke-Remote $applyScript 'transactional boot-fix install' 300)
    if ([string]$applyState.unit_sha256 -ne $unitSha256 -or
        [string]$applyState.enabled -ne 'enabled') {
        throw 'The installed P7 unit identity or enablement did not verify.'
    }

    $stage = 'cold_stop_start'
    Write-Host '[p7-boot] performing the real GCE stop/start proof...'
    $beforeBootId = [string]$preState.boot_id
    Set-P7Power stop
    $coldStartedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Set-P7Power start
    Wait-P7Ssh
    $coldState = Wait-StackGreen $coldStartedUtc
    $publicStatus = Wait-PublicHealth
    $coldPassed =
        (Test-StackGreen $coldState) -and
        [string]$coldState.boot_id -ne $beforeBootId -and
        $publicStatus -eq 200

    $stage = 'forced_retry_recovery'
    Write-Host '[p7-boot] injecting one first-start failure for automatic retry proof...'
    $retryStartedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $retryDropIn =
        '/etc/systemd/system/comfy-lumberjacks-p7.service.d/' +
        '90-c10b-retry-proof.conf'
    $retryMarker = '/run/comfy-p7-c10b-retry-once'
    $retryScript = @"
set -euo pipefail
dropin='$retryDropIn'
marker='$retryMarker'
sudo install -d -m 0755 "`$(dirname "`$dropin")"
printf '%s\n' '[Service]' "ExecStartPre=/bin/sh -c 'if test ! -e `$marker; then echo C10B_RETRY_PROOF_FIRST_START_FAILURE >&2; touch `$marker; exit 75; fi'" | sudo tee "`$dropin" >/dev/null
sudo rm -f "`$marker"
sudo systemctl daemon-reload
sudo systemctl restart '$service' || true
"@
    $retryDropInInstalled = $true
    [void](Invoke-Remote $retryScript 'one-shot retry fault injection' 300)
    $retryState = Wait-StackGreen $retryStartedUtc
    $retryJournal = Invoke-Remote @"
set -u
sudo journalctl -u '$service' --since '$retryStartedUtc' --no-pager
"@ 'retry-recovery journal capture' 120
    $retryEvidencePath = Write-EvidenceText 'retry-recovery-journal.txt' $retryJournal
    $retryFaultObserved = $retryJournal -match 'C10B_RETRY_PROOF_FIRST_START_FAILURE'
    # A manual restart begins a new activation and may reset NRestarts, so
    # compare against the activation's semantic floor rather than the cold
    # boot's unrelated counter. The injected marker proves which first start
    # failed; NRestarts >= 1 proves systemd initiated the successor start.
    $retryCounterAdvanced = [int]$retryState.restart_count -ge 1
    $retryPassed =
        (Test-StackGreen $retryState) -and
        $retryFaultObserved -and
        $retryCounterAdvanced

    $stage = 'remove_retry_probe'
    Write-Host '[p7-boot] retry recovered; removing the one-shot probe...'
    [void](Invoke-Remote @"
set -e
sudo rm -f '$retryDropIn' '$retryMarker'
sudo systemctl daemon-reload
"@ 'retry-probe cleanup' 90)
    $retryDropInInstalled = $false
    $postCleanupState = Get-StackState $retryStartedUtc
    $verifiedVm = Get-P7Vm

    $checks = [ordered]@{
        preserved_pre_fix_evidence = Test-Path -LiteralPath $capturePath -PathType Leaf
        world_save_non_hazardous = -not $worldHazard -and -not $postGuardWorldHazard
        instance_id_stable = [string]$verifiedVm.id -eq [string]$initialVm.id
        instance_running = [string]$verifiedVm.status -eq 'RUNNING'
        cold_boot_id_changed = [string]$coldState.boot_id -ne $beforeBootId
        retry_same_verified_boot =
            [string]$retryState.boot_id -eq [string]$coldState.boot_id
        cold_unit_enabled = [string]$coldState.unit_enabled -eq 'enabled'
        cold_unit_active = [string]$coldState.unit_active -eq 'active'
        cold_all_seven_containers_running =
            [int]$coldState.container_total -eq 7 -and
            [int]$coldState.container_running -eq 7
        cold_zero_created_containers = [int]$coldState.container_created -eq 0
        cold_postgres_healthy = [string]$coldState.postgres_health -eq 'healthy'
        cold_gateway_health = [string]$coldState.gateway_health -eq 'true'
        cold_public_tls_200 = $publicStatus -eq 200
        cold_valheim_ready = [string]$coldState.server_ready -eq 'true'
        cold_stop_start_passed = $coldPassed
        retry_fault_observed = $retryFaultObserved
        retry_restart_counter_recorded = $retryCounterAdvanced
        retry_recovery_passed = $retryPassed
        retry_probe_removed = -not $retryDropInInstalled
        post_cleanup_stack_green = Test-StackGreen $postCleanupState
    }
    $failed = @($checks.GetEnumerator() | Where-Object { -not [bool]$_.Value })
    $acceptance = [ordered]@{
        schema_version = 1
        receipt_type = 'p7_boot_determinism_acceptance'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
        project = $project
        zone = $zone
        instance = $instance
        instance_id = [string]$verifiedVm.id
        verified_boot_id = [string]$retryState.boot_id
        run_id = $RunId
        unit_sha256 = $unitSha256
        unit_backup_root = [string]$applyState.backup_root
        pre_fix_evidence_path = $capturePath
        retry_evidence_path = $retryEvidencePath
        pre_fix = $preState
        cold_cycle = [ordered]@{
            before_boot_id = $beforeBootId
            after = $coldState
            public_health_status = $publicStatus
        }
        retry = [ordered]@{
            after = $retryState
            post_cleanup = $postCleanupState
        }
        checks = $checks
        failed_checks = @($failed | ForEach-Object Key)
    }
    Write-Receipt $acceptance
    if ($failed.Count -gt 0) {
        throw "P7 boot determinism proof failed: $(@($failed | ForEach-Object Key) -join ', ')"
    }
} catch {
    if ($retryDropInInstalled) {
        try {
            [void](Invoke-Remote @"
set +e
sudo rm -f '/etc/systemd/system/comfy-lumberjacks-p7.service.d/90-c10b-retry-proof.conf' '/run/comfy-p7-c10b-retry-once'
sudo systemctl daemon-reload
"@ 'retry-probe emergency cleanup' 90)
        } catch { }
    }
    if ($acceptance) { throw }
    $currentVm = $null
    try { $currentVm = Get-P7Vm } catch { }
    $failure = [ordered]@{
        schema_version = 1
        receipt_type = 'p7_boot_determinism_acceptance'
        generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
        result = 'failed'
        project = $project
        zone = $zone
        instance = $instance
        instance_id = [string]$initialVm.id
        verified_boot_id = ''
        run_id = $RunId
        failed_stage = $stage
        error = $_.Exception.Message
        current_status = if ($currentVm) { [string]$currentVm.status } else { 'unknown' }
        checks = [ordered]@{
            cold_stop_start_passed = $false
            retry_recovery_passed = $false
        }
    }
    Write-Receipt $failure
    throw
}
