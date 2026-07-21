<#
.SYNOPSIS
  Regression coverage for the release-gate defect: proves the admitted mod release actually reaches
  the shipped Gateway IMAGE, that an uncut image is caught rather than promoted, and that a
  promotable build cannot ship the sentinel at all.

.DESCRIPTION
  THE DEFECT THIS LOCKS DOWN.

  The Gateway bakes the mod release it admits into its assembly as AssemblyMetadata. The release cut
  verified that by reading src/Game.Gateway/bin/Release/**/Game.Gateway.dll - a locally published
  DLL that never ships. The artifact that ships is the Docker image, and the Dockerfile published
  the Gateway with no such property, so the shipped /app/Game.Gateway.dll carried no
  LumberjacksExpectedModRelease attribute at all. Since Game.Gateway.csproj defaults the property to
  "dev" and ValheimReleaseIdentity maps "dev" -> null, and null disables the gate, the cut went
  green while the deployed Gateway admitted anything at all.

  Three cases, in the order they matter:

    A  The regression proper. Build with a real release id, then read it back OUT OF THE IMAGE and
       require an exact match. Before the Dockerfile fix this could not pass, because the value
       never reached the image - the attribute was simply absent. This is the case that would catch
       the defect coming back.

    B  The verifier must FAIL an uncut image, and say "dev" while doing it. A verifier that passes
       the pre-fix condition is not a gate, it is decoration. This case tests the test.

    C  A promotable build (LUMBERJACKS_REQUIRE_RELEASE=1) must not be able to produce an image at
       all when handed the sentinel or a malformed id - `docker build` itself must fail. A and B
       catch a bad image after it exists; C means it never exists.

  Everything is built under the lumberjacks-gateway-regression: tag prefix and torn down in the
  finally, so a failed run does not leave images or containers behind.

  Docker builds take minutes. This script is slow on a cold cache and that is expected.

.PARAMETER LumberjacksRoot
  Repo root holding the Dockerfile under test.
#>
[CmdletBinding()]
param(
  [string] $LumberjacksRoot = 'C:\work\Lumberjacks'
)

$ErrorActionPreference = 'Stop'

$TagPrefix = 'lumberjacks-gateway-regression'
$CaseATag  = "${TagPrefix}:case-a"
$CaseBTag  = "${TagPrefix}:case-b"
$CaseC1Tag = "${TagPrefix}:case-c-sentinel"
$CaseC2Tag = "${TagPrefix}:case-c-malformed"

# Deliberately not a real release id: nothing this script builds should ever be mistaken for
# something promotable, but it must still satisfy the Dockerfile's id pattern.
$CaseARelease = 'm9-regress-20260719-r1'

$verifier = Join-Path $PSScriptRoot 'Test-GatewayImageRelease.ps1'
if (!(Test-Path -LiteralPath $verifier))       { throw "missing verifier: $verifier" }
if (!(Test-Path -LiteralPath $LumberjacksRoot)) { throw "missing repo: $LumberjacksRoot" }

# Every tag we may have created, cleaned up unconditionally at the end.
$createdTags = @($CaseATag, $CaseBTag, $CaseC1Tag, $CaseC2Tag)
$results     = [ordered]@{}

function Invoke-GatewayBuild {
  <#
    Builds the gateway target. Returns the exit code instead of throwing, because two of the three
    cases EXPECT a non-zero build.

    Note the absence of 2>&1: docker writes build progress to stderr, and in Windows PowerShell 5.1
    merging a native command's stderr into the success stream wraps every line in an ErrorRecord,
    which under $ErrorActionPreference='Stop' would abort the run on a perfectly healthy build. Let
    it stream to the console and judge by exit code.
  #>
  param([string] $Tag, [string[]] $BuildArgs)

  $argv = @('build', '--target', 'gateway', '-t', $Tag) + $BuildArgs + @('.')
  Write-Host "`n  docker $($argv -join ' ')" -ForegroundColor DarkGray

  Push-Location $LumberjacksRoot
  try {
    & docker @argv
    return $LASTEXITCODE
  }
  finally { Pop-Location }
}

function Invoke-Verifier {
  <#
    Runs the authoritative verifier as a child process, which is exactly how an operator runs it,
    and gives an unambiguous exit code plus its console text for assertion.
  #>
  param([string] $Image, [string] $Expected)

  $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier `
            -Image $Image -ExpectedRelease $Expected
  $code = $LASTEXITCODE
  $text = ($out | Out-String)
  Write-Host $text
  return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

try {
  Write-Host '=====================================================================' -ForegroundColor Cyan
  Write-Host ' Gateway image release-identity regression' -ForegroundColor Cyan
  Write-Host " repo: $LumberjacksRoot" -ForegroundColor Cyan
  Write-Host '=====================================================================' -ForegroundColor Cyan

  # --- Case A: the release id must survive into the shipped image ---------------------------------
  Write-Host "`n[CASE A] a promotable build's release id reaches the IMAGE" -ForegroundColor Yellow
  Write-Host "         expect: image bakes exactly '$CaseARelease'"

  $codeA = Invoke-GatewayBuild -Tag $CaseATag -BuildArgs @(
    '--build-arg', "LUMBERJACKS_EXPECTED_MOD_RELEASE=$CaseARelease",
    '--build-arg', 'LUMBERJACKS_REQUIRE_RELEASE=1'
  )

  if ($codeA -ne 0) {
    $results['A'] = @{ Pass = $false; Why = "docker build failed (exit $codeA) for a valid promotable build" }
  }
  else {
    $verA = Invoke-Verifier -Image $CaseATag -Expected $CaseARelease
    if ($verA.ExitCode -eq 0) {
      $results['A'] = @{ Pass = $true;  Why = "image bakes '$CaseARelease' and the verifier confirmed it" }
    } else {
      $results['A'] = @{ Pass = $false; Why = "verifier rejected an image that should carry '$CaseARelease' (exit $($verA.ExitCode))" }
    }
  }

  # --- Case B: the verifier must catch the pre-fix condition --------------------------------------
  Write-Host "`n[CASE B] an uncut image (no build args) is REJECTED, naming the sentinel" -ForegroundColor Yellow
  Write-Host '         expect: verifier exits non-zero and says "dev"'

  $codeB = Invoke-GatewayBuild -Tag $CaseBTag -BuildArgs @()

  if ($codeB -ne 0) {
    $results['B'] = @{ Pass = $false; Why = "docker build failed (exit $codeB); a plain local build must still succeed" }
  }
  else {
    $verB = Invoke-Verifier -Image $CaseBTag -Expected $CaseARelease
    # Both halves matter: failing for the wrong reason would still "pass" a bare exit-code check.
    # Match the sentinel branch's own wording, not a bare "dev" - "dev" appears in plenty of
    # incidental text, and this assertion is meant to prove WHICH branch fired.
    $namesSentinel = $verB.Output -match "uncut 'dev' sentinel"
    if ($verB.ExitCode -ne 0 -and $namesSentinel) {
      $results['B'] = @{ Pass = $true;  Why = 'verifier rejected the uncut image and named the dev sentinel' }
    } elseif ($verB.ExitCode -eq 0) {
      $results['B'] = @{ Pass = $false; Why = 'verifier PASSED an uncut image - it would have passed the original defect' }
    } else {
      $results['B'] = @{ Pass = $false; Why = 'verifier failed the uncut image but never named the dev sentinel' }
    }
  }

  # --- Case C: a promotable build cannot ship the sentinel ----------------------------------------
  Write-Host "`n[CASE C] a promotable build REFUSES the sentinel and a malformed id" -ForegroundColor Yellow
  Write-Host '         expect: docker build itself exits non-zero, twice'

  $codeC1 = Invoke-GatewayBuild -Tag $CaseC1Tag -BuildArgs @(
    '--build-arg', 'LUMBERJACKS_EXPECTED_MOD_RELEASE=dev',
    '--build-arg', 'LUMBERJACKS_REQUIRE_RELEASE=1'
  )
  Write-Host "  sentinel build exit code : $codeC1"

  $codeC2 = Invoke-GatewayBuild -Tag $CaseC2Tag -BuildArgs @(
    '--build-arg', 'LUMBERJACKS_EXPECTED_MOD_RELEASE=not-a-release',
    '--build-arg', 'LUMBERJACKS_REQUIRE_RELEASE=1'
  )
  Write-Host "  malformed build exit code: $codeC2"

  if ($codeC1 -ne 0 -and $codeC2 -ne 0) {
    $results['C'] = @{ Pass = $true;  Why = 'both the sentinel and the malformed id failed the build' }
  } elseif ($codeC1 -eq 0 -and $codeC2 -eq 0) {
    $results['C'] = @{ Pass = $false; Why = 'BOTH promotable builds succeeded; the Dockerfile guard is not running' }
  } elseif ($codeC1 -eq 0) {
    $results['C'] = @{ Pass = $false; Why = "a promotable build accepted the 'dev' sentinel" }
  } else {
    $results['C'] = @{ Pass = $false; Why = "a promotable build accepted the malformed id 'not-a-release'" }
  }

  # --- summary ------------------------------------------------------------------------------------
  Write-Host "`n=====================================================================" -ForegroundColor Cyan
  Write-Host ' RESULTS' -ForegroundColor Cyan
  Write-Host '=====================================================================' -ForegroundColor Cyan

  $labels = [ordered]@{
    A = "release id reaches the shipped image"
    B = "uncut image is rejected, naming the sentinel"
    C = "promotable build refuses sentinel + malformed id"
  }

  $failed = 0
  foreach ($k in $labels.Keys) {
    $r = $results[$k]
    if ($null -eq $r) { $r = @{ Pass = $false; Why = 'case did not run' } }
    if ($r.Pass) {
      Write-Host ("  PASS  Case {0}: {1}" -f $k, $labels[$k]) -ForegroundColor Green
      Write-Host ("          {0}" -f $r.Why) -ForegroundColor DarkGray
    } else {
      $failed++
      Write-Host ("  FAIL  Case {0}: {1}" -f $k, $labels[$k]) -ForegroundColor Red
      Write-Host ("          {0}" -f $r.Why) -ForegroundColor Red
    }
  }

  Write-Host ''
  if ($failed -gt 0) {
    Write-Host "$failed of $($labels.Count) cases FAILED." -ForegroundColor Red
    exit 1
  }
  Write-Host "all $($labels.Count) cases passed; the release id is carried by the artifact that ships." -ForegroundColor Green
  exit 0
}
finally {
  # Unconditional teardown. A regression run that leaves images behind turns into a disk-space
  # incident on a build box, and a stale case-a image would let a later run pass on yesterday's
  # artifact.
  #
  # 'Continue' for the duration of cleanup, and it is not cosmetic. Under 'Stop' a native command
  # that merely writes to stderr raises a terminating error, and a terminating error thrown from a
  # finally block REPLACES the exit code the try block already chose - so a run with all three cases
  # green exited 1 because `docker rmi` was handed a tag that does not exist. Case C never produces
  # an image (that is the point of Case C), so that path is guaranteed, not hypothetical. Teardown
  # must never be able to change the verdict.
  $ErrorActionPreference = 'Continue'

  Write-Host "`ncleaning up throwaway images and containers..." -ForegroundColor DarkGray
  foreach ($tag in $createdTags) {
    # `images -q` is silent and exits 0 for a tag that does not exist, unlike `rmi`/`ps --filter
    # ancestor=`, which complain. Ask first, then only remove what is actually there.
    $imgId = (& docker images -q $tag | Where-Object { $_ } | Select-Object -First 1)
    if (-not $imgId) { continue }

    $stale = & docker ps -a --filter "ancestor=$tag" --format '{{.ID}}'
    foreach ($id in @($stale | Where-Object { $_ })) {
      & docker rm -f $id | Out-Null
    }
    & docker rmi -f $tag | Out-Null
  }
  Write-Host 'cleanup done.' -ForegroundColor DarkGray
}
