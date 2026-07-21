<#
.SYNOPSIS
  Cuts a GATEWAY-ONLY release: builds a new Gateway image that continues to admit the existing
  frozen mod, and proves from the image itself that it does.

.DESCRIPTION
  WHY THERE ARE TWO IDENTITIES HERE, AND WHY THEY DIFFER.

  New-ReleaseCut.ps1 cuts both sides at once: one id, stamped into the mod and into the Gateway, and
  the two must agree. That is right when both sides are actually changing.

  A Gateway-only promotion is not that. The Gateway changes; the mod does not. The mod artifact is
  frozen - 0.5.31 is built, hashed, recorded in a manifest, and pinned by every guest package
  already handed out. Reusing the both-sides cut here would rewrite ComfyNetworkSense.cs's ReleaseId
  const and rebuild the mod, which INVALIDATES that frozen artifact: same source, new hash, new id,
  and every guest package pinned to the old one is now pinned to something that no longer exists.
  Volunteers who already installed would be holding a mod the new Gateway refuses. Shipping a
  Gateway fix would have broken every existing install - the exact opposite of the intent.

  So this script keeps the two identities apart, deliberately:

    image_release_id     - what THIS Gateway image is. New every Gateway cut.
    admitted_mod_release - what mod release this Gateway ADMITS. Stays pinned to the frozen mod
                           across many Gateway cuts.

  They are SUPPOSED to differ. A reviewer seeing m4-... admitting m1-... is looking at a correct
  Gateway-only cut, not a mistake. The invariant is not "the two ids match" - it is "the id baked
  into the shipped image equals the mod release we intend to admit", and that is what gets checked
  below, from the image.

  THE GATE IS THE IMAGE. New-ReleaseCut.ps1 used to verify the baked value by reading
  src/Game.Gateway/bin/Release/**/Game.Gateway.dll - a local publish that never ships. The artifact
  that ships is the image, the Dockerfile published the Gateway without the property, and so the
  shipped assembly carried no release attribute at all; "dev" maps to null and null DISABLES the
  gate. The cut passed while the deployed Gateway admitted anything. Test-GatewayImageRelease.ps1
  reads the image, and that is the only check here allowed to say the cut is good.

.PARAMETER ImageReleaseId
  Identity of the Gateway image being cut, e.g. m4-clean-20260719-r1.

.PARAMETER AdmittedModRelease
  The frozen mod release this Gateway admits, e.g. m1-clean-20260717-r1. NOT rebuilt, NOT edited.

.PARAMETER LumberjacksRoot
  Repo root holding the Dockerfile.

.EXAMPLE
  .\New-GatewayReleaseCut.ps1 -ImageReleaseId m4-clean-20260719-r1 `
                              -AdmittedModRelease m1-clean-20260717-r1
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $ImageReleaseId,
  [Parameter(Mandatory)][string] $AdmittedModRelease,
  [string] $LumberjacksRoot = 'C:\work\Lumberjacks'
)

$ErrorActionPreference = 'Stop'

# <milestone>-<label>-<yyyymmdd>-r<n>, enforced on BOTH ids because both become an artifact's
# identity: one names the image, the other is compiled into it and decides who may join.
#
# -cnotmatch, not -notmatch: PowerShell's -match is case-INSENSITIVE, while the Dockerfile guard
# uses `grep -E`, which is not. Left case-insensitive, 'M1-Clean-...' would sail past this check and
# then be rejected minutes later inside the build, or - worse on a day someone relaxes the guard -
# get baked in a casing the runtime comparison (StringComparison.Ordinal) would not match. Reject it
# here, where the error is cheap and legible.
$pattern = '^m\d+-[a-z0-9]+-\d{8}-r\d+$'
foreach ($pair in @(
    @{ Name = 'ImageReleaseId';     Value = $ImageReleaseId },
    @{ Name = 'AdmittedModRelease'; Value = $AdmittedModRelease })) {

  if ($pair.Value -eq 'dev') {
    throw "$($pair.Name): 'dev' is the uncut sentinel and cannot be promoted."
  }
  if ($pair.Value -cnotmatch $pattern) {
    throw ("$($pair.Name) '$($pair.Value)' does not match <milestone>-<label>-<yyyymmdd>-r<n>, " +
           'e.g. m4-clean-20260719-r1 (lower-case)')
  }
}

if (!(Test-Path -LiteralPath $LumberjacksRoot)) { throw "missing repo: $LumberjacksRoot" }
$verifier = Join-Path $PSScriptRoot 'Test-GatewayImageRelease.ps1'
if (!(Test-Path -LiteralPath $verifier)) { throw "missing verifier: $verifier" }

$imageTag = "lumberjacks-gateway:$ImageReleaseId"

Write-Host '=====================================================================' -ForegroundColor Cyan
Write-Host ' Gateway-only release cut' -ForegroundColor Cyan
Write-Host '=====================================================================' -ForegroundColor Cyan
Write-Host "  image_release_id     : $ImageReleaseId"
Write-Host "  admitted_mod_release : $AdmittedModRelease"
Write-Host "  image tag            : $imageTag"

# Say this out loud every run. The single most expensive mistake available on this path is someone
# "helpfully" re-cutting or redeploying the mod to make the two ids match.
Write-Host "`n  THE MOD STAYS FROZEN." -ForegroundColor Yellow
Write-Host '  This script does not touch ComfyNetworkSense.cs, does not rebuild the mod, and does' -ForegroundColor Yellow
Write-Host '  not run deploy-network-sense.ps1. The frozen 0.5.31 artifact and every guest package' -ForegroundColor Yellow
Write-Host '  pinned to it stay valid precisely because nothing here rewrites the mod ReleaseId.' -ForegroundColor Yellow

# --- 1. build the image, failing closed on a sentinel or malformed id ----------------------------
# LUMBERJACKS_REQUIRE_RELEASE=1 makes the Dockerfile refuse to produce an image at all for an
# empty/'dev'/malformed id, so a promotable build cannot ship a disabled gate even if this script
# were bypassed.
Write-Host "`nbuilding gateway image..." -ForegroundColor Cyan

Push-Location $LumberjacksRoot
try {
  # No 2>&1: docker writes build progress to stderr, and in Windows PowerShell 5.1 merging a native
  # command's stderr into the success stream turns every progress line into an ErrorRecord, which
  # under $ErrorActionPreference='Stop' aborts a perfectly healthy build.
  & docker build --target gateway -t $imageTag `
      --build-arg "LUMBERJACKS_EXPECTED_MOD_RELEASE=$AdmittedModRelease" `
      --build-arg 'LUMBERJACKS_REQUIRE_RELEASE=1' `
      .
  if ($LASTEXITCODE -ne 0) { throw "gateway image build failed (exit $LASTEXITCODE); nothing from this cut should ship" }
}
finally { Pop-Location }

# --- 2. THE GATE: ask the IMAGE ------------------------------------------------------------------
# Not the source, not the build log, not bin/Release. The image is what ships, so the image is what
# gets asked. If this fails, the cut fails.
Write-Host "`nverifying release identity from the image (AUTHORITATIVE)..." -ForegroundColor Cyan

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier `
    -Image $imageTag -ExpectedRelease $AdmittedModRelease
if ($LASTEXITCODE -ne 0) {
  Write-Host "`n  FAIL: $imageTag does not admit '$AdmittedModRelease'." -ForegroundColor Red
  throw 'image release identity check failed; nothing from this cut should ship'
}

# --- 3. hand back both identities, clearly labelled ----------------------------------------------
Write-Host "`n=====================================================================" -ForegroundColor Green
Write-Host ' GATEWAY CUT OK' -ForegroundColor Green
Write-Host '=====================================================================' -ForegroundColor Green
Write-Host '  For the release manifest:' -ForegroundColor Green
Write-Host "    image_release_id     : $ImageReleaseId"      -ForegroundColor Green
Write-Host "    admitted_mod_release : $AdmittedModRelease"  -ForegroundColor Green
Write-Host ''
Write-Host '  These two are DELIBERATELY DIFFERENT. This is a Gateway-only promotion: a new Gateway' -ForegroundColor Green
Write-Host '  image that continues to admit the existing frozen mod. They are not meant to match,' -ForegroundColor Green
Write-Host '  and making them match would mean re-cutting the mod and invalidating the frozen' -ForegroundColor Green
Write-Host '  artifact plus every guest package pinned to it.' -ForegroundColor Green

Write-Host @"

Next, and NOT done by this script:
  1. capture-release-manifest.ps1 -> record BOTH ids: image_release_id '$ImageReleaseId' and
     admitted_mod_release '$AdmittedModRelease'. Record them as two fields; a manifest with one
     'release_id' is what makes people think the mod moved.
  2. Push/re-pin the gateway image and roll it out (deploy-gateway.ps1).
  3. Do NOT run deploy-network-sense.ps1. The mod did not change; redeploying it would replace a
     frozen, hashed artifact for no reason and break the pin every guest package relies on.
  4. StrictReleaseEnabled can stay as it is: this cut does not change which mod is admitted, only
     which Gateway build admits it.

The check that gates this cut is Test-GatewayImageRelease.ps1 reading /app/Game.Gateway.dll out of
the built image. bin/Release is not consulted anywhere on this path, by design.
"@
