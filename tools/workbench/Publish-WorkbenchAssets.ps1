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
- `node scripts/workbench.mjs check` must pass (page not stale, invariants hold).
- Every zip's actual SHA-256 must equal the sha256 in workbench.json's access block —
  the public page can never claim a hash the artifact does not have.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\workbench\Publish-WorkbenchAssets.ps1
#>
[CmdletBinding()]
param(
    [string] $SshTarget = 'comfy-p7',
    [string] $RemoteRoot = '/mnt/comfy-p7/lumberjacks/roadmap'
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
$pointerLocal = Join-Path ([System.IO.Path]::GetTempPath()) 'workbench-tools.json'
[System.IO.File]::WriteAllText($pointerLocal, ($pointer | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))

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
$encoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($remoteScript))
$argLine = ($zipArgs | ForEach-Object { "'$_'" }) -join ' '
& ssh $SshTarget "echo $encoded | base64 -d | sudo bash -s -- '$RemoteRoot' '$stamp' '$htmlHash' $argLine"
if ($LASTEXITCODE -ne 0) { throw 'remote publish failed' }

[pscustomobject]@{
    workbench_html_sha256 = $htmlHash
    tools                 = $pointerTools | ForEach-Object { "$($_.id) $($_.sha256.Substring(0,12))..." }
    remote_root           = $RemoteRoot
    verify_next           = "curl -s -o NUL -w '%{http_code}' https://comfy-p7.duckdns.org/workbench (expect 200 after the route deploy)"
}
