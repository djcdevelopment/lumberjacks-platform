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
  # A cut must not ship anything until the identity check has passed, and this build is no
  # exception: the csproj's CopyAssembly target auto-copies every build into the live Steam
  # client's plugins folder (that is the OMEN client-deploy convenience, keep it elsewhere).
  # Here it would land a release-id DLL on the client BEFORE the check below has run - a failed
  # cut would already have shipped locally. Point the copy at a path that does not exist so the
  # target's Exists() gate stays shut.
  & dotnet build $modProject -c Release -v quiet --nologo -p:PluginOutputPath=C:\__comfy_cut_no_plugin_copy__
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
# flag was passed. The question is what the artifacts actually carry, because that is what ships and
# what the gate compares at runtime.
#
# The metadata reader lives in lib/ReleaseIdentity.ps1, shared with Test-GatewayImageRelease.ps1.
# Two readers of the same value is how a release gate ends up with two answers and a preference for
# the convenient one; there is exactly one implementation, and both callers use it. (It also carries
# the Windows PowerShell 5.1 note about why the assemblies are vendored rather than loaded from the
# GAC - read it before "simplifying" that.)
$releaseIdentityLib = Join-Path $PSScriptRoot 'lib\ReleaseIdentity.ps1'
if (!(Test-Path -LiteralPath $releaseIdentityLib)) {
  # Same reasoning as the reader's own load-loudly rule: a cut that cannot verify what it built must
  # stop, not continue and report success it never established.
  throw "missing metadata reader: $releaseIdentityLib"
}
. $releaseIdentityLib

if (-not $WhatIf) {
  # --- 3a. the mod side: unchanged, and still authoritative --------------------------------------
  # The mod ships as this very DLL - it is copied to clients as-is - so reading it here is reading
  # the shipped artifact. Nothing about the Gateway problem below applies to it.
  $modBaked = Get-AssemblyMetadataValue -DllPath $modDll -Key 'LumberjacksModReleaseId'

  Write-Host "`n--- what the MOD artifact says ---"
  Write-Host ("  mod ComfyNetworkSense.dll : {0}" -f $modBaked)

  if ($modBaked -ne $ReleaseId) {
    Write-Host "  FAIL: mod DLL says '$modBaked', cut is '$ReleaseId'" -ForegroundColor Red
    throw 'release identity check failed; nothing from this cut should ship'
  }
  Write-Host "  OK: mod artifact carries '$ReleaseId'" -ForegroundColor Green

  # --- 3b. the gateway's bin/Release DLL: ADVISORY ONLY, NOT A GATE ------------------------------
  # This read used to be the Gateway's release check, and it was checking the wrong object. The
  # Gateway does not ship as a loose DLL; it ships as a Docker image. bin/Release is a local publish
  # that never leaves this machine, and it was green while the IMAGE carried no release attribute at
  # all - because the Dockerfile published the Gateway without the property, and "dev" maps to null,
  # and null DISABLES the gate. A passing check on an object nobody deploys is worse than no check:
  # it is a gate that reports armed while being off.
  #
  # It is kept only because a disagreement here is a useful early hint that the local build and the
  # image were made from different inputs. It CANNOT fail the cut. The image below decides.
  $gatewayDll = Get-ChildItem -Path (Join-Path $LumberjacksRoot 'src\Game.Gateway\bin\Release') `
      -Filter 'Game.Gateway.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

  Write-Host "`n--- gateway bin/Release (ADVISORY - not the gate, this DLL never ships) ---"
  if (-not $gatewayDll) {
    Write-Host '  advisory: no bin/Release DLL found; skipping (this does not affect the cut).'
  } else {
    $gatewayLocalBaked = Get-AssemblyMetadataValue -DllPath $gatewayDll.FullName -Key 'LumberjacksExpectedModRelease'
    Write-Host ("  local Game.Gateway.dll : {0}" -f $gatewayLocalBaked)
    if ($gatewayLocalBaked -ne $ReleaseId) {
      Write-Host "  advisory only: local DLL says '$gatewayLocalBaked', cut is '$ReleaseId'." -ForegroundColor Yellow
      Write-Host '  NOT failing the cut on this - bin/Release is not what ships. The image check decides.' -ForegroundColor Yellow
    }
  }

  # --- 3c. THE GATEWAY GATE: the IMAGE ------------------------------------------------------------
  # Build the artifact that actually ships, with the release args, and read the id back out of it.
  # LUMBERJACKS_REQUIRE_RELEASE=1 additionally makes the Dockerfile refuse to produce an image at all
  # for an empty/'dev'/malformed id, so this fails at build time rather than verification time when
  # the id is wrong in an obvious way.
  $imageTag = "lumberjacks-gateway:$ReleaseId"
  Write-Host "`nbuilding gateway IMAGE $imageTag (this is the artifact that ships)..." -ForegroundColor Cyan

  Push-Location $LumberjacksRoot
  try {
    # No 2>&1: docker writes progress to stderr, and in WinPS 5.1 merging a native command's stderr
    # into the success stream makes every progress line an ErrorRecord, which under
    # $ErrorActionPreference='Stop' aborts a healthy build.
    & docker build --target gateway -t $imageTag `
        --build-arg "LUMBERJACKS_EXPECTED_MOD_RELEASE=$ReleaseId" `
        --build-arg 'LUMBERJACKS_REQUIRE_RELEASE=1' `
        .
    if ($LASTEXITCODE -ne 0) { throw "gateway image build failed (exit $LASTEXITCODE); nothing from this cut should ship" }
  }
  finally { Pop-Location }

  Write-Host "`n--- what the gateway IMAGE says (AUTHORITATIVE) ---"
  $verifier = Join-Path $PSScriptRoot 'Test-GatewayImageRelease.ps1'
  if (!(Test-Path -LiteralPath $verifier)) { throw "missing verifier: $verifier" }

  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier `
      -Image $imageTag -ExpectedRelease $ReleaseId
  if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAIL: the gateway IMAGE does not admit '$ReleaseId'." -ForegroundColor Red
    Write-Host '        THE TWO SIDES DISAGREE - this gateway would reject this mod.' -ForegroundColor Red
    throw 'release identity check failed; nothing from this cut should ship'
  }

  Write-Host "`n  OK: the mod artifact and the gateway IMAGE both carry '$ReleaseId'" -ForegroundColor Green
  Write-Host "  gateway image: $imageTag" -ForegroundColor Green
}

Write-Host @"

Next, and NOT done by this script:
  1. Commit the mod ReleaseId change (it is a source edit and belongs in the release commit).
  2. build-release-bundle.ps1 / capture-release-manifest.ps1 -> record '$ReleaseId' + artifact hashes.
     Record the gateway IMAGE (lumberjacks-gateway:$ReleaseId) as the gateway artifact. bin/Release
     is not an artifact; it is a local build output that never ships.
  3. validate-release-bundle.ps1, then run-promotion-drill.ps1.
  4. Deploy: deploy-network-sense.ps1 (mod) and re-pin the gateway image built above.
  5. Only then flip StrictReleaseEnabled on the window - it must stay OFF until the cut has landed
     everywhere, because a mod predating mod_release_id sends nothing and absence rejects.

The gateway's release identity is gated on the IMAGE, by Test-GatewayImageRelease.ps1 reading
/app/Game.Gateway.dll out of it. The bin/Release read this script still prints is ADVISORY and
cannot fail the cut: that DLL never ships, and checking it is how a disabled gate passed review.
Re-verify any gateway image at any time with:
  .\Test-GatewayImageRelease.ps1 -Image lumberjacks-gateway:$ReleaseId -ExpectedRelease $ReleaseId

GATEWAY-ONLY CUTS DO NOT USE THIS SCRIPT. Use New-GatewayReleaseCut.ps1 instead. This script rewrites
ComfyNetworkSense.cs and rebuilds the mod, which for a Gateway-only promotion would invalidate the
frozen mod artifact and every guest package pinned to it. New-GatewayReleaseCut.ps1 keeps the two
identities apart - a new image_release_id admitting the unchanged admitted_mod_release - and leaves
the mod alone:
  .\New-GatewayReleaseCut.ps1 -ImageReleaseId m4-clean-20260719-r1 -AdmittedModRelease $ReleaseId

Rebuild-to-verify (plan risk 12): ROOT CAUSE FOUND 2026-07-18. The .NET 8 SDK's implicit
source-control tasks embed the git HEAD sha in the portable PDB (only when origin is a recognized
host like github; a local-path clone embeds nothing), and the PDB checksum rides in the DLL's
debug directory. So the DLL's identity bytes change on EVERY COMMIT with unchanged source, and a
local clone can never match this tree - that was the whole clone-vs-worktree mystery. Proven:
-p:EnableSourceControlManagerQueries=false makes a clone and this tree build byte-identical DLLs.
Consequence for cuts as ordered today (build, THEN commit): the shipped DLL embeds the sha of the
release commit's PARENT, so no checkout of the release commit can ever rebuild it. The fix is a
decision, not code here: pin the queries off in the csproj (hash = source alone, loses embedded
provenance), or reorder to commit-first-build-second (keeps provenance, rebuild needs same origin
URL). Until one is chosen these hashes attest what this machine built at this exact HEAD.
"@
