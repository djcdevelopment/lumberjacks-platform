<#
.SYNOPSIS
Publish the Community Workbench assets (workbench.html + tool zips + tools.json pointer)
to the P7 roadmap mount. After the one-time image deploy that adds the /workbench routes,
THIS script is the entire update path — no image build, no restart.

.DESCRIPTION
Pattern cloned from infra/gcp/p7/scripts/Publish-Modpack.ps1: scp to /tmp, then a
base64-encoded remote script (BOM-proof) verifies every SHA-256 remote-side, installs
with 0644, and atomically mv's the pointer + page into place.

Publish gates (all fail closed):
- The provenance inputs (workbench.json + workbench.mjs) must be committed and the page must
  carry the production "Published from <sha7>" stamp — a preview artifact never publishes.
- `node scripts/workbench.mjs check` must pass (page not stale, invariants hold).
- Every zip's actual SHA-256 must equal the sha256 in workbench.json's access block —
  the public page can never claim a hash the artifact does not have.
- The emitted tools.json must round-trip into the shape WorkbenchDownloadEndpoints reads,
  checked against Lumberjacks/tests/Game.Gateway.Tests/Fixtures/workbench-pointer.sample.json.
  The first two gates each check one side alone, which is how a 'tools' key once shipped.
- Live destinations must verify (scripts/workbench-verify-live.mjs --pre-publish): the Discord
  invite and threads, every GitHub URL, and the site routes. After the upload, the full
  --post-publish pass re-verifies downloads and the served page hash against this render.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\workbench\Publish-WorkbenchAssets.ps1
#>
[CmdletBinding()]
param(
    [string] $SshTarget = 'comfy-p7',
    [string] $RemoteRoot = '/mnt/comfy-p7/lumberjacks/roadmap',
    # The origin the published page is reached at. The live surface today is the AM4 funnel;
    # comfy-p7.duckdns.org belongs to the future P7 cutover recipe.
    [string] $PublicBaseUrl = 'https://am4.tail8e749c.ts.net'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lumberjacks = Join-Path $root 'Lumberjacks'
$htmlPath = Join-Path $lumberjacks 'src\Game.Gateway\Community\workbench.html'
$dataPath = Join-Path $lumberjacks 'docs\workbench\workbench.json'
$dist = Join-Path $PSScriptRoot 'dist'

# tool id (the /workbench/downloads/{id} route) -> zip file in dist/
$zipMap = [ordered]@{
    'quest-picker'        = 'quest-picker.zip'
    'community-telemetry' = 'telemetry-starter.zip'
}

# Gate 0: production provenance. A publish is a claim about a commit, so the provenance inputs
# must be clean and the page must carry the "Published from <sha7>" production stamp. The
# generator's check (Gate 1) enforces the same rule from the other side; this gate exists so a
# publish failure says "publish" and names the dirty files, and so a preview artifact cannot
# reach the upload path even if the gates below ever drift apart.
if (-not (Test-Path $htmlPath)) { throw "workbench.html missing at $htmlPath - run npm run workbench:render first" }
Push-Location $root
try {
    $dirtyInputs = & git status --porcelain -- 'Lumberjacks/docs/workbench/workbench.json' 'Lumberjacks/scripts/workbench.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'git status failed - publishing requires a git checkout' }
    if ($dirtyInputs) {
        throw "provenance inputs have uncommitted changes - commit and re-render before publishing:`n$($dirtyInputs -join "`n")"
    }
} finally { Pop-Location }
if ([System.IO.File]::ReadAllText($htmlPath) -notmatch 'Published from [0-9a-f]{7}') {
    throw 'workbench.html does not carry a production provenance stamp ("Published from <sha7>") - render from a clean tree before publishing'
}

# Gate 1: generator check (stale page or broken invariant blocks publish)
Push-Location $lumberjacks
try {
    & node scripts/workbench.mjs check
    if ($LASTEXITCODE -ne 0) { throw 'workbench check failed - fix and re-render before publishing' }
} finally { Pop-Location }

# Gate 2: every published zip hash must match the page's claim
$data = [System.IO.File]::ReadAllText($dataPath) | ConvertFrom-Json
$pointerTools = @()
foreach ($id in $zipMap.Keys) {
    $zipFile = Join-Path $dist $zipMap[$id]
    if (-not (Test-Path $zipFile)) { throw "missing $zipFile - run New-WorkbenchZip.ps1 -Tool $id first" }
    $actual = (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $tool = $data.tools | Where-Object { $_.id -eq $id }
    if ($null -eq $tool) { throw "tool id '$id' not present in workbench.json" }
    if ($tool.access.kind -ne 'site-download') { throw "tool '$id' access.kind is '$($tool.access.kind)', not site-download" }
    if ($tool.access.sha256 -ne $actual) {
        throw "HASH MISMATCH for '$id': workbench.json says $($tool.access.sha256), artifact is $actual. Rebuild or update the page - do not publish a page that lies."
    }
    $pointerTools += [ordered]@{
        id         = $id
        file       = $zipMap[$id]
        sha256     = $actual
        size_bytes = (Get-Item $zipFile).Length
    }
}

# The key is 'downloads', not 'tools'. WorkbenchDownloadEndpoints deserializes into
# DownloadPointer(int schema_version, List<DownloadEntry>? downloads) and treats a null list as an
# invalid pointer, so a 'tools' key parses into a pointer with no downloads and every
# /workbench/downloads/{id} answers 503. Caught on the first real deploy; nothing tests this shape.
$pointer = [ordered]@{
    schema_version = 1
    generated_utc  = (Get-Date).ToUniversalTime().ToString('o')
    downloads      = $pointerTools
}
$pointerJson = $pointer | ConvertTo-Json -Depth 4

# Gate 3: the emitted pointer must round-trip into the shape the endpoint reads.
# Gates 1 and 2 both passed on the deploy that shipped a 'tools' key, because each checked one side
# in isolation. This one re-parses the bytes about to be uploaded and compares their key shape to
# Fixtures/workbench-pointer.sample.json - the same sample Game.Gateway.Tests deserializes into
# WorkbenchDownloadEndpoints.DownloadPointer. Producer and consumer are then pinned to one file, and
# a rename on either side fails here or in the test suite rather than on a deploy.
$contractPath = Join-Path $lumberjacks 'tests\Game.Gateway.Tests\Fixtures\workbench-pointer.sample.json'
if (-not (Test-Path $contractPath)) { throw "pointer contract sample missing at $contractPath" }
$contract = [System.IO.File]::ReadAllText($contractPath) | ConvertFrom-Json
$roundTripped = $pointerJson | ConvertFrom-Json

$expectedKeys = $contract.PSObject.Properties.Name | Sort-Object
$actualKeys = $roundTripped.PSObject.Properties.Name | Sort-Object
if (Compare-Object $expectedKeys $actualKeys) {
    throw "pointer key mismatch: contract has [$($expectedKeys -join ', ')], emitted [$($actualKeys -join ', ')]. The endpoint reads 'downloads'; a different key parses into an empty pointer and every /workbench/downloads/{id} answers 503."
}
if ($roundTripped.schema_version -ne 1) { throw "pointer schema_version is '$($roundTripped.schema_version)', not 1" }

$expectedEntryKeys = $contract.downloads[0].PSObject.Properties.Name | Sort-Object
$emitted = @($roundTripped.downloads)
if ($emitted.Count -ne $pointerTools.Count) {
    throw "pointer round-trip lost entries: built $($pointerTools.Count), parsed $($emitted.Count)"
}
foreach ($entry in $emitted) {
    $entryKeys = $entry.PSObject.Properties.Name | Sort-Object
    if (Compare-Object $expectedEntryKeys $entryKeys) {
        throw "pointer entry '$($entry.id)' has keys [$($entryKeys -join ', ')], contract expects [$($expectedEntryKeys -join ', ')]"
    }
    if ($entry.sha256 -notmatch '^[0-9a-f]{64}$') { throw "pointer entry '$($entry.id)' sha256 is not 64 lowercase hex chars" }
    if ($entry.size_bytes -le 0) { throw "pointer entry '$($entry.id)' has size_bytes '$($entry.size_bytes)'" }
}

# Gate 4: live destination verification. Everything the page asks a visitor to click — the
# Discord invite and threads, every GitHub URL, the site's own routes — must answer correctly
# before the upload, because a publish is also a claim that its destinations exist. Post-upload
# the full pass (downloads + served hash) runs against the same origin.
Push-Location $lumberjacks
try {
    & node scripts/workbench-verify-live.mjs --pre-publish --base-url $PublicBaseUrl
    if ($LASTEXITCODE -ne 0) { throw 'live destination verification failed - see captures/workbench-verify-live.json; fix the destination or the page before publishing' }
} finally { Pop-Location }

$pointerLocal = Join-Path ([System.IO.Path]::GetTempPath()) 'workbench-tools.json'
[System.IO.File]::WriteAllText($pointerLocal, $pointerJson, [System.Text.UTF8Encoding]::new($false))

$htmlHash = (Get-FileHash -LiteralPath $htmlPath -Algorithm SHA256).Hash.ToLowerInvariant()
$stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)

# upload everything to /tmp first
& scp $htmlPath "${SshTarget}:/tmp/workbench-$stamp.html"
if ($LASTEXITCODE -ne 0) { throw 'workbench.html upload failed' }
& scp $pointerLocal "${SshTarget}:/tmp/workbench-tools-$stamp.json"
if ($LASTEXITCODE -ne 0) { throw 'tools.json upload failed' }
$zipArgs = @()
foreach ($id in $zipMap.Keys) {
    $zipName = $zipMap[$id]
    & scp (Join-Path $dist $zipName) "${SshTarget}:/tmp/workbench-zip-$stamp-$zipName"
    if ($LASTEXITCODE -ne 0) { throw "$zipName upload failed" }
    $zipHash = ($pointerTools | Where-Object { $_.file -eq $zipName }).sha256
    $zipArgs += "$zipName=$zipHash"
}

$remoteScript = @'
set -euo pipefail
root="$1"; stamp="$2"; html_hash="$3"; shift 3
mkdir -p "$root"
actual="$(sha256sum "/tmp/workbench-$stamp.html" | awk '{print $1}')"
test "$actual" = "$html_hash"
for pair in "$@"; do
  name="${pair%%=*}"; expected="${pair##*=}"
  actual="$(sha256sum "/tmp/workbench-zip-$stamp-$name" | awk '{print $1}')"
  test "$actual" = "$expected"
  install -m 0644 "/tmp/workbench-zip-$stamp-$name" "$root/$name"
  rm -f "/tmp/workbench-zip-$stamp-$name"
done
install -m 0644 "/tmp/workbench-$stamp.html" "$root/workbench.html.tmp"
mv -f "$root/workbench.html.tmp" "$root/workbench.html"
install -m 0644 "/tmp/workbench-tools-$stamp.json" "$root/tools.json.tmp"
mv -f "$root/tools.json.tmp" "$root/tools.json"
rm -f "/tmp/workbench-$stamp.html" "/tmp/workbench-tools-$stamp.json"
printf 'published workbench %s\n' "$html_hash"
'@
# Normalise CRLF before encoding, exactly as Promote-GatewayImage.ps1 does. A here-string picks up
# whatever line endings the .ps1 was checked out with, and git hands Windows clones CRLF — so the
# remote bash reads `set -euo pipefail\r`, reports "pipefail: invalid option name", and the publish
# dies after the uploads. Whether this script worked depended on the checkout, not on the code.
$encoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(($remoteScript -replace "`r`n", "`n")))
$argLine = ($zipArgs | ForEach-Object { "'$_'" }) -join ' '
& ssh $SshTarget "echo $encoded | base64 -d | sudo bash -s -- '$RemoteRoot' '$stamp' '$htmlHash' $argLine"
if ($LASTEXITCODE -ne 0) { throw 'remote publish failed' }

# Post-publish verification: the full pass, now that the upload is what the origin serves —
# downloads stream with the claimed digest/size and the served page hash equals this render.
Push-Location $lumberjacks
try {
    & node scripts/workbench-verify-live.mjs --post-publish --base-url $PublicBaseUrl
    if ($LASTEXITCODE -ne 0) { throw 'post-publish verification failed - the live origin does not match what was just published; see captures/workbench-verify-live.json' }
} finally { Pop-Location }

[pscustomobject]@{
    workbench_html_sha256 = $htmlHash
    tools                 = $pointerTools | ForEach-Object { "$($_.id) $($_.sha256.Substring(0,12))..." }
    remote_root           = $RemoteRoot
    verified              = "verify-live post-publish PASS against $PublicBaseUrl (receipt: captures/workbench-verify-live.json)"
}
