<#
.SYNOPSIS
  Reads LumberjacksExpectedModRelease back out of a Gateway DOCKER IMAGE and fails loudly unless it
  is a real release id equal to -ExpectedRelease. This is the authoritative release-identity gate.

.DESCRIPTION
  WHY THE IMAGE, AND NOT bin/Release.

  The Gateway bakes the mod release it admits into its own assembly as AssemblyMetadata, so the
  expected value travels with the artifact instead of being typed at an operator. Good design,
  defeated by where we checked it: New-ReleaseCut.ps1 verified the baked value by reading
  src/Game.Gateway/bin/Release/**/Game.Gateway.dll - a locally published DLL that NEVER SHIPS.
  What ships is the image, and the Dockerfile published the Gateway with no such property, so the
  shipped /app/Game.Gateway.dll carried no LumberjacksExpectedModRelease attribute at all.
  Confirmed empirically against the live container.

  The failure mode is the ugly one. Game.Gateway.csproj defaults the property to "dev", and
  ValheimReleaseIdentity.ReadBakedValue() maps "dev" -> null, and null DISABLES the release gate
  rather than rejecting every join. So the cut went green against a DLL nobody deploys while the
  deployed image quietly admitted anything at all: a gate that looks armed, reports armed, and is
  not. Nothing is louder than a passing check, which is why the check has to read the artifact that
  actually leaves the building.

  So: pull the DLL out of the image, ask it what it carries, and refuse anything that is absent,
  sentinel, or not the id we meant. No build log, no source, no bin/Release.

.PARAMETER Image
  Gateway image to inspect, e.g. lumberjacks-gateway:m4-clean-20260719-r1. Must exist locally.

.PARAMETER ExpectedRelease
  The mod release id this image must admit, e.g. m1-clean-20260717-r1.

.EXAMPLE
  .\Test-GatewayImageRelease.ps1 -Image lumberjacks-gateway:m4-clean-20260719-r1 `
                                 -ExpectedRelease m1-clean-20260717-r1
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $Image,
  [Parameter(Mandatory)][string] $ExpectedRelease
)

$ErrorActionPreference = 'Stop'

# One implementation of the metadata reader, shared with New-ReleaseCut.ps1, so the cut and this
# verifier cannot answer the same question differently.
. (Join-Path $PSScriptRoot 'lib\ReleaseIdentity.ps1')

# The sentinel Game.Gateway.csproj defaults to and ValheimReleaseIdentity maps to null.
$Uncut = 'dev'

$containerId = $null
$tempDll     = Join-Path ([IO.Path]::GetTempPath()) ("gateway-release-{0}.dll" -f [guid]::NewGuid())

try {
  Write-Host "inspecting image : $Image"
  Write-Host "expecting        : $ExpectedRelease"

  # `create`, never `run`: we want the filesystem, not the process. Starting a Gateway here would
  # have it reach for a database and a port for no reason.
  #
  # Deliberately NOT `2>&1`: in Windows PowerShell 5.1 merging a native command's stderr into the
  # success stream wraps each line in an ErrorRecord, which under $ErrorActionPreference='Stop'
  # terminates right here - so the clean diagnostic below never prints and the operator gets a
  # NativeCommandError stack trace instead. Let docker's own stderr go to the console and judge the
  # command by its exit code.
  $containerId = (& docker create $Image | Select-Object -Last 1)
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) {
    Write-Host "  FAIL: could not create a container from '$Image'." -ForegroundColor Red
    Write-Host "        Is the image built and present locally? (docker images $Image)" -ForegroundColor Red
    exit 1
  }
  $containerId = $containerId.Trim()

  & docker cp "${containerId}:/app/Game.Gateway.dll" $tempDll
  if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAIL: could not copy /app/Game.Gateway.dll out of '$Image'." -ForegroundColor Red
    Write-Host "        Is this actually a Gateway image (docker build --target gateway)?" -ForegroundColor Red
    exit 1
  }

  $baked = Get-AssemblyMetadataValue -DllPath $tempDll -Key 'LumberjacksExpectedModRelease'

  Write-Host "`n--- what the IMAGE says ---"
  Write-Host ("  {0} -> /app/Game.Gateway.dll : {1}" -f $Image, $(if ($null -eq $baked) { '<attribute absent>' } elseif ($baked -eq '') { '<empty>' } else { $baked }))

  # Three distinct failures, because they mean three different things to whoever reads the log.

  # 1. Absent. This is the defect above verbatim: a publish that never received the property, so
  #    the attribute was never emitted. The runtime gate is OFF and nothing said so.
  if ($null -eq $baked) {
    Write-Host "  FAIL: this image carries NO LumberjacksExpectedModRelease attribute at all." -ForegroundColor Red
    Write-Host "        The Gateway publish did not receive -p:LumberjacksExpectedModRelease, so the" -ForegroundColor Red
    Write-Host "        release gate is DISABLED in this image while appearing configured." -ForegroundColor Red
    Write-Host "        Build with: --build-arg LUMBERJACKS_EXPECTED_MOD_RELEASE=$ExpectedRelease --build-arg LUMBERJACKS_REQUIRE_RELEASE=1" -ForegroundColor Red
    exit 1
  }

  # 2. Present but uncut. The attribute exists, which makes it look configured, but "dev" (and an
  #    empty value) is exactly what ValheimReleaseIdentity maps to null. Same disabled gate,
  #    different cause: someone built without the release args rather than without the property.
  if ([string]::IsNullOrWhiteSpace($baked) -or $baked -eq $Uncut) {
    Write-Host "  FAIL: this image is baked with the uncut '$Uncut' sentinel (value: '$baked')." -ForegroundColor Red
    Write-Host "        ValheimReleaseIdentity maps '$Uncut'/empty to null, which DISABLES the release" -ForegroundColor Red
    Write-Host "        gate. An uncut image must never be promoted." -ForegroundColor Red
    exit 1
  }

  # 3. Real id, wrong one. The gate is armed and will work - against the wrong release. Shipping
  #    this rejects the very mod it was meant to admit.
  if ($baked -ne $ExpectedRelease) {
    Write-Host "  FAIL: this image admits '$baked', but the cut expects '$ExpectedRelease'." -ForegroundColor Red
    Write-Host "        The gate is armed against the WRONG release: deployed as-is it would reject" -ForegroundColor Red
    Write-Host "        the mod it is supposed to admit." -ForegroundColor Red
    exit 1
  }

  Write-Host "  OK: image admits mod release '$baked'" -ForegroundColor Green
  Write-Host "`nrelease identity confirmed from the SHIPPED artifact (not bin/Release)." -ForegroundColor Green
  exit 0
}
finally {
  # Both of these must happen even when the checks above exit non-zero, or a failed verification
  # leaves a stopped container and a stray DLL behind every time it runs.
  #
  # 'Continue' while tearing down: under 'Stop' a native command writing to stderr raises a
  # terminating error, and a terminating error thrown from a finally block REPLACES the exit code
  # the try block already chose - which would let a cleanup hiccup report a passing image as failed,
  # or mask the real reason a failing one failed. Cleanup must never change the verdict.
  $ErrorActionPreference = 'Continue'

  if ($containerId) {
    & docker rm -f $containerId | Out-Null
  }
  if (Test-Path -LiteralPath $tempDll) {
    Remove-Item -LiteralPath $tempDll -Force -ErrorAction SilentlyContinue
  }
}
