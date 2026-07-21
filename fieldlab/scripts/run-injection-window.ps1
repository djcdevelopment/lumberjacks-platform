# P5/I4 inbound-injection gate driver. PowerShell 5.1, ASCII only.
#
#   .\fieldlab\scripts\run-injection-window.ps1 -Stage deploy
#   .\fieldlab\scripts\run-injection-window.ps1 -Stage arm -WindowId i4-w1
#   # Derek fresh-launches Valheim and joins; auto-route moves to the fixture.
#   .\fieldlab\scripts\run-injection-window.ps1 -Stage stage -WindowId i4-w1
#   .\fieldlab\scripts\run-injection-window.ps1 -Stage malformed -WindowId i4-w1
#   .\fieldlab\scripts\run-injection-window.ps1 -Stage gate -WindowId i4-w1
#   .\fieldlab\scripts\run-injection-window.ps1 -Stage disarm

param(
  [Parameter(Mandatory = $true)]
  [ValidateSet("deploy", "arm", "stage", "malformed", "gate", "disarm")]
  [string]$Stage,
  [string]$WindowId = "",
  [string]$Prefab = "Wood",
  [long]$AuthorityId = 5497853135698,
  [uint32]$UidId = 1,
  [float]$X = 9376,
  [float]$Y = 105,
  [float]$Z = 544,
  [int]$ActiveSeconds = 90,
  [int]$ProbeSeconds = 150
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$lumberjacks = "C:\work\Lumberjacks"
$dotnet9 = "C:\work\dotnet9\dotnet.exe"
$python = "C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe"
$valheim = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
$localPlugin = Join-Path $valheim "BepInEx\plugins\ComfyNetworkSense.dll"
$localCfg = Join-Path $valheim "BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg"
$routeDir = Join-Path $valheim "BepInEx\config\comfy-network-sense"
$clientLog = Join-Path $valheim "BepInEx\LogOutput.log"
$builtDll = Join-Path $repo "network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll"
$routeSource = Join-Path $repo "fieldlab\routes\i4-injection.tsv"
$gatewayDll = Join-Path $lumberjacks "src\Game.Gateway\bin\Release\net9.0\Game.Gateway.dll"
$gatewayOut = Join-Path $repo "fieldlab\status\lumberjacks-gateway-stdout.log"
$gatewayErr = Join-Path $repo "fieldlab\status\lumberjacks-gateway-stderr.log"
$am4 = if ($env:FIELDLAB_AM4_SSH) { $env:FIELDLAB_AM4_SSH } else { "derek@am4" }
$container = if ($env:FIELDLAB_VALHEIM_CONTAINER) { $env:FIELDLAB_VALHEIM_CONTAINER } else { "comfy-valheim-server-am4-valheim-server-1" }
$bepinex = if ($env:FIELDLAB_AM4_BEPINEX) { $env:FIELDLAB_AM4_BEPINEX } else { "~/comfy-valheim-lab/server-state/config/bepinex" }
$remoteCfg = "$bepinex/djcdevelopment.valheim.comfynetworksense.cfg"
$remotePlugin = "$bepinex/plugins/ComfyNetworkSense.dll"
$env:PYTHONPATH = Join-Path $repo "network\mcp"
$env:PYTHONUTF8 = "1"

function Set-CfgValue {
  param([string]$Path, [string]$Section, [string]$Key, [string]$Value)
  $lines = @(Get-Content -LiteralPath $Path)
  $pattern = '^' + [regex]::Escape($Key) + ' = .*$'
  $currentSection = ""
  $sectionSeen = $false
  $written = $false
  $updated = New-Object System.Collections.Generic.List[string]
  foreach ($line in $lines) {
    if ($line -match '^\[(.+)\]$') {
      if ($currentSection -eq $Section -and -not $written) {
        $updated.Add("$Key = $Value")
        $updated.Add("")
        $written = $true
      }
      $currentSection = $Matches[1]
      if ($currentSection -eq $Section) { $sectionSeen = $true }
      $updated.Add($line)
      continue
    }
    if ($line -match $pattern) {
      # Replace only the intended section and remove stale duplicates elsewhere.
      if ($currentSection -eq $Section -and -not $written) {
        $updated.Add("$Key = $Value")
        $written = $true
      }
      continue
    }
    $updated.Add($line)
  }
  if (-not $written) {
    if (-not $sectionSeen) {
      $updated.Add("")
      $updated.Add("[$Section]")
    }
    $updated.Add("$Key = $Value")
  }
  Set-Content -LiteralPath $Path -Value $updated -Encoding UTF8
}

function Invoke-Tool {
  param([string]$Expr)
  & $python -c "import json; from comfy_gateway.toolsurface import valheim as v; print(json.dumps($Expr, indent=2, default=str))"
}

function Wait-Http {
  param([string]$Url)
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $deadline) {
    try { Invoke-RestMethod -Uri $Url -TimeoutSec 3 | Out-Null; return } catch { Start-Sleep 2 }
  }
  throw "Timed out waiting for $Url"
}

function Restart-ServerAndWait {
  ssh $am4 "docker restart $container" | Out-Null
  $deadline = (Get-Date).AddMinutes(4)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 15
    $up = ssh $am4 "docker logs --since 4m $container 2>&1 | grep -c 'Game server connected'"
    if ([int]$up -ge 1) { Write-Host "am4 world up." -ForegroundColor Green; return }
  }
  throw "am4 server did not reach Game server connected"
}

if ($Stage -in @("arm", "stage", "malformed", "gate") -and -not $WindowId) {
  throw "-WindowId is required for stage '$Stage'"
}

switch ($Stage) {
  "deploy" {
    dotnet build (Join-Path $repo "network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj") -c Release
    & $dotnet9 test (Join-Path $lumberjacks "tests\Game.Gateway.Tests\Game.Gateway.Tests.csproj") -c Release
    Copy-Item -LiteralPath $builtDll -Destination $localPlugin -Force
    scp $builtDll "${am4}:$remotePlugin"

    $listeners = @(Get-NetTCPConnection -LocalPort 4000 -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
      $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
      if ($process.CommandLine -notmatch 'Game.Gateway') {
        throw "Port 4000 is owned by an unexpected process: $($process.CommandLine)"
      }
      Stop-Process -Id $listener.OwningProcess -Force
    }
    Start-Process -FilePath $dotnet9 -ArgumentList @($gatewayDll, '--urls', 'http://0.0.0.0:4000') `
      -WorkingDirectory $lumberjacks -WindowStyle Hidden `
      -RedirectStandardOutput $gatewayOut -RedirectStandardError $gatewayErr
    Wait-Http "http://127.0.0.1:4000/health"
    Write-Host "0.5.13 installed on OMEN + am4; Release gateway restarted." -ForegroundColor Green
  }
  "arm" {
    New-Item -ItemType Directory -Force $routeDir | Out-Null
    Copy-Item -LiteralPath $routeSource -Destination (Join-Path $routeDir "i4-injection.tsv") -Force
    Set-CfgValue $localCfg "Netcode" "zdoInjectionEnabled" "true"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionPrefabs" $Prefab
    Set-CfgValue $localCfg "Netcode" "zdoInjectionEndpoint" "http://127.0.0.1:4000"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionWindowId" $WindowId
    Set-CfgValue $localCfg "Netcode" "zdoInjectionClientId" "omen"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionAuthorityId" "$AuthorityId"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionPollSeconds" "1"
    Set-CfgValue $localCfg "Netcode" "zdoInjectionActiveSeconds" "$ActiveSeconds"
    Set-CfgValue $localCfg "Netcode" "netcodeProbeAutoStartEnabled" "true"
    Set-CfgValue $localCfg "Netcode" "netcodeProbeAutoStopSeconds" "$ProbeSeconds"
    Set-CfgValue $localCfg "Automation" "autoRehearsalRouteFile" "i4-injection.tsv"
    Set-CfgValue $localCfg "Automation" "coupleAutoRehearsalToNetcodeProbe" "true"
    Set-CfgValue $localCfg "Automation" "routeGodFlySafeguard" "true"

    ssh $am4 "sed -i -e 's/^zdoInjectionEnabled = .*/zdoInjectionEnabled = false/' -e 's/^zdoRedirectEnabled = .*/zdoRedirectEnabled = false/' -e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = false/' $remoteCfg"
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:4000/valheim/zdo-injection/reset/$WindowId" | ConvertTo-Json
    Invoke-Tool "v.valheim_save_integrity(action='snapshot', label='$WindowId')"
    Restart-ServerAndWait
    Write-Host ">>> Fresh-launch Valheim and join wary.fool. Auto-route targets ($X,$Y,$Z)." -ForegroundColor Yellow
    Write-Host ">>> Then run -Stage stage -WindowId $WindowId" -ForegroundColor Yellow
  }
  "stage" {
    $body = @{
      window_id = $WindowId
      command = @{ command_id = "synthetic-wood-1"; action = "upsert"; prefab = $Prefab
        uid_user = $AuthorityId; uid_id = $UidId; owner = $AuthorityId
        owner_rev = 1; data_rev = 1; pos = @($X, $Y, $Z) }
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:4000/valheim/zdo-injection/stage" `
      -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 5
  }
  "malformed" {
    $cases = [string[]] @(
      '{"window_id":"__WINDOW__","command":{"command_id":"bad-action","action":"delete","prefab":"Wood","uid_user":5497853135698,"uid_id":2,"owner":5497853135698,"owner_rev":1,"data_rev":1,"pos":[9376,105,544]}}'.Replace('__WINDOW__', $WindowId)
      '{"window_id":"__WINDOW__","command":{"command_id":"bad-pos","action":"upsert","prefab":"Wood","uid_user":5497853135698,"uid_id":3,"owner":5497853135698,"owner_rev":1,"data_rev":1,"pos":[999999,0,0]}}'.Replace('__WINDOW__', $WindowId)
      '{"window_id":"__WINDOW__","command":{"command_id":"truncated"'.Replace('__WINDOW__', $WindowId)
    )
    foreach ($case in $cases) {
      $code = curl.exe -s -o NUL -w "%{http_code}" -H "Content-Type: application/json" `
        --data-binary $case "http://127.0.0.1:4000/valheim/zdo-injection/stage"
      if ($code -notin @("400", "422")) { throw "Malformed fixture unexpectedly returned HTTP $code" }
      Write-Host "Malformed fixture rejected: HTTP $code" -ForegroundColor Green
    }
  }
  "gate" {
    Invoke-Tool "v.valheim_zdo_injection_gate(window_id='$WindowId')"
    Invoke-Tool "v.valheim_save_integrity(action='check')"
    $bad = Select-String -Path $clientLog -Pattern 'INVALID_MESSAGE|socket error|Got connection error' -ErrorAction SilentlyContinue
    if ($bad) { $bad | Select-Object -Last 10 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red } }
    else { Write-Host "Client stability log clean." -ForegroundColor Green }
  }
  "disarm" {
    Set-CfgValue $localCfg "Netcode" "zdoInjectionEnabled" "false"
    Set-CfgValue $localCfg "Automation" "autoRehearsalRouteFile" "teleport-route.tsv"
    ssh $am4 "sed -i -e 's/^zdoInjectionEnabled = .*/zdoInjectionEnabled = false/' -e 's/^zdoRedirectEnabled = .*/zdoRedirectEnabled = false/' -e 's/^ownershipPinEnabled = .*/ownershipPinEnabled = false/' $remoteCfg"
    Write-Host "Injection/redirect/pin flags are off. Quit/relaunch clears the client-only synthetic ZDO." -ForegroundColor Green
  }
}
