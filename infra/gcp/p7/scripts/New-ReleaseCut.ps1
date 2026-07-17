<#
.SYNOPSIS
  Cuts a mod+Gateway release: sets the release id ONCE, builds both sides from it, and refuses to
  hand back artifacts that disagree about which release they are.

.DESCRIPTION
  M1 risk 9 wants both sides to derive their release identity from one record. They cannot share a
  mechanism: the Gateway takes an MSBuild property (LumberjacksExpectedModRelease) baked as
  AssemblyMetadata, while the mod sets GenerateAssemblyInfo=false and so carries a const projected
  into AssemblyMetadata by its AssemblyInfo.cs. Two mechanisms means two places a cut must touch.

  A cut that sets one and forgets the other ships a Gateway that rejects the very mod it shipped
  with. That is worse than the drift the release gate exists to catch: the gate would be doing its
  job, loudly, against its own release, and nobody would find out until a volunteer could not join.
  It is also invisible to review - both diffs look right in isolation.

  So this script is the record. The id is typed once, here, and the last thing it does is read the
  id back OUT of both compiled artifacts and compare them. Not the source, not the build log - the
  DLLs. If they disagree, the cut fails and nothing ships.

.PARAMETER ReleaseId
  e.g. m1-clean-20260717-r2. Convention: <milestone>-clean-<yyyymmdd>-r<n>.

.PARAMETER WhatIf
  Print what would change; touch nothing.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $ReleaseId,
  [string] $ComfyRoot       = 'C:\work\comfy',
  [string] $LumberjacksRoot = 'C:\work\Lumberjacks',
  [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# <milestone>-clean-<date>-r<n>. Enforced because this string becomes an artifact's identity: it
# lands in the manifest, in two DLLs, and in the gate that decides admission. A typo here is a
# release nobody can name later.
if ($ReleaseId -notmatch '^m\d+-[a-z0-9]+-\d{8}-r\d+$') {
  throw "ReleaseId '$ReleaseId' does not match <milestone>-<label>-<yyyymmdd>-r<n>, e.g. m1-clean-20260717-r2"
}
if ($ReleaseId -eq 'dev') { throw "'dev' is the uncut sentinel and cannot be cut as a release." }

$modProject  = Join-Path $ComfyRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj'
$modSource   = Join-Path $ComfyRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.cs'
$modDll      = Join-Path $ComfyRoot 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
$gatewayProj = Join-Path $LumberjacksRoot 'src\Game.Gateway\Game.Gateway.csproj'

foreach ($p in @($modProject, $modSource, $gatewayProj)) {
  if (!(Test-Path -LiteralPath $p)) { throw "missing: $p" }
}

# --- 1. the mod: the const is the only place it can live (GenerateAssemblyInfo=false) -----------
$src = Get-Content -LiteralPath $modSource -Raw
$pattern = '(public const string ReleaseId = ")([^"]*)(";)'
if ($src -notmatch $pattern) { throw "could not find the ReleaseId const in $modSource" }
$current = [regex]::Match($src, $pattern).Groups[2].Value
Write-Host "mod ReleaseId : $current -> $ReleaseId"

if (-not $WhatIf) {
  # -NoNewline + the file's own bytes: this repo pins network/mod/**/*.cs to LF (see
  # network/mod/.gitattributes) because the mod DLL's hash is line-ending sensitive and the SHIPPED
  # artifact was built from LF. Set-Content would rewrite every line ending and silently change the
  # hash of the thing being cut.
  $updated = [regex]::Replace($src, $pattern, "`${1}$ReleaseId`${3}")
  [IO.File]::WriteAllText($modSource, $updated, (New-Object Text.UTF8Encoding($false)))
}

# --- 2. build both, each from the same $ReleaseId ------------------------------------------------
if (-not $WhatIf) {
  Write-Host "`nbuilding mod (net48, Release)..."
  & dotnet build $modProject -c Release -v quiet --nologo
  if ($LASTEXITCODE -ne 0) { throw 'mod build failed' }

  # The Gateway targets net9.0 and this workstation ships only the .NET 8 SDK, so a bare
  # `dotnet build` here fails with NETSDK1045 rather than producing an artifact. The net9 lane on
  # this box is the sdk:9.0 container with the repo mounted; prefer a local SDK when one exists so
  # this is not Docker-dependent on a machine that does not need it.
  $hasNet9 = @(& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^9\.' }).Count -gt 0
  Write-Host "building gateway (net9.0, Release) with -p:LumberjacksExpectedModRelease=$ReleaseId ..."
  if ($hasNet9) {
    & dotnet build $gatewayProj -c Release -v quiet --nologo -p:LumberjacksExpectedModRelease=$ReleaseId
    if ($LASTEXITCODE -ne 0) { throw 'gateway build failed' }
  } else {
    Write-Host '  no local .NET 9 SDK; building through mcr.microsoft.com/dotnet/sdk:9.0'
    $rel = 'src/Game.Gateway/Game.Gateway.csproj'
    & docker run --rm -v "${LumberjacksRoot}:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 `
        dotnet build $rel -c Release -v quiet --nologo "-p:LumberjacksExpectedModRelease=$ReleaseId"
    if ($LASTEXITCODE -ne 0) { throw 'gateway build failed (sdk:9.0 container)' }
  }
}

# --- 3. THE CHECK: ask the ARTIFACTS, not the source --------------------------------------------
# Reading source back would only prove the regex worked. Reading the build log would only prove a
# flag was passed. The question is what the DLLs actually carry, because that is what ships and
# what the gate compares at runtime.
function Get-AssemblyMetadataValue {
  param([string] $DllPath, [string] $Key)
  if (!(Test-Path -LiteralPath $DllPath)) { throw "artifact not found: $DllPath" }
  Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction SilentlyContinue
  $stream = [IO.File]::OpenRead($DllPath)
  try {
    $pe = New-Object System.Reflection.PortableExecutable.PEReader($stream)
    try {
      $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
      foreach ($h in $md.CustomAttributes) {
        $attr = $md.GetCustomAttribute($h)
        try {
          $blob = $md.GetBlobReader($attr.Value)
          [void]$blob.ReadUInt16()
          $k = $blob.ReadSerializedString()
          $v = $blob.ReadSerializedString()
          if ($k -eq $Key) { return $v }
        } catch { }
      }
    } finally { $pe.Dispose() }
  } finally { $stream.Dispose() }
  return $null
}

if (-not $WhatIf) {
  $gatewayDll = Get-ChildItem -Path (Join-Path $LumberjacksRoot 'src\Game.Gateway\bin\Release') `
      -Filter 'Game.Gateway.dll' -Recurse | Select-Object -First 1
  if (-not $gatewayDll) { throw 'gateway artifact not found after build' }

  $modBaked     = Get-AssemblyMetadataValue -DllPath $modDll            -Key 'LumberjacksModReleaseId'
  $gatewayBaked = Get-AssemblyMetadataValue -DllPath $gatewayDll.FullName -Key 'LumberjacksExpectedModRelease'

  Write-Host "`n--- what the ARTIFACTS say ---"
  Write-Host ("  mod     ComfyNetworkSense.dll : {0}" -f $modBaked)
  Write-Host ("  gateway Game.Gateway.dll      : {0}" -f $gatewayBaked)

  $problems = @()
  if ($modBaked     -ne $ReleaseId) { $problems += "mod DLL says '$modBaked', cut is '$ReleaseId'" }
  if ($gatewayBaked -ne $ReleaseId) { $problems += "gateway DLL says '$gatewayBaked', cut is '$ReleaseId'" }
  if ($modBaked -ne $gatewayBaked)  { $problems += "THE TWO SIDES DISAGREE - this gateway would reject this mod" }

  if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  FAIL: $_" -ForegroundColor Red }
    throw 'release identity check failed; nothing from this cut should ship'
  }
  Write-Host "  OK: both artifacts agree on '$ReleaseId'" -ForegroundColor Green
}

Write-Host @"

Next, and NOT done by this script:
  1. Commit the mod ReleaseId change (it is a source edit and belongs in the release commit).
  2. build-release-bundle.ps1 / capture-release-manifest.ps1 -> record '$ReleaseId' + artifact hashes.
  3. validate-release-bundle.ps1, then run-promotion-drill.ps1.
  4. Deploy: deploy-network-sense.ps1 (mod) and the gateway image re-pin.
  5. Only then flip StrictReleaseEnabled on the window - it must stay OFF until the cut has landed
     everywhere, because a mod predating mod_release_id sends nothing and absence rejects.

Rebuild-to-verify is NOT established (plan risk 12): a clone and this working tree still build
different DLLs, so these hashes attest what this machine built, not what the commit builds.
"@
