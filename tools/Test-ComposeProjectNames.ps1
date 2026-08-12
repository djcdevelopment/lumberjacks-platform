[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
function Test-ComposeFile([string]$Path) {
    $names = @(Get-Content -LiteralPath $Path | Where-Object { $_ -match '^name:\s*' })
    return $names.Count -eq 1 -and $names[0] -match '^name:\s*lumberjacks-[a-z0-9-]+\s*$'
}

$files = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
    $_.Name -match '(?:docker-)?compose.*\.ya?ml$' -and $_.FullName -notmatch '[\\/]\.git[\\/]'
})
$bad = @($files | Where-Object { -not (Test-ComposeFile $_.FullName) })
if ($bad.Count -gt 0) {
    $bad.FullName | Write-Error
    exit 1
}
if ($SelfTest) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-g3-' + [guid]::NewGuid().ToString('N') + '.yml')
    try {
        [IO.File]::WriteAllText($fixture, "name: copied-project`nservices: {}`n")
        if (Test-ComposeFile $fixture) { throw 'G3 self-test accepted a foreign project name' }
    }
    finally { Remove-Item -LiteralPath $fixture -Force }
}
[pscustomobject]@{ Schema = 'lumberjacks-boundary-guard/v1'; Guard = 'G3'; Verdict = 'passed'; Files = $files.Count; SelfTest = [bool]$SelfTest } |
    ConvertTo-Json -Compress
