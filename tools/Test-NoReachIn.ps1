[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
$rootPath = [IO.Path]::GetFullPath($Root)
$extensions = @('.csproj', '.props', '.targets', '.ps1', '.py', '.mjs', '.js', '.yml', '.yaml', '.sh')
$excludedRelative = @(
    'tools\Test-NoReachIn.ps1',
    'tools\Assert-RepoIdentity.ps1',
    'tools\workbench\Test-WorkbenchZipPrivacy.ps1'
)

function Test-Tree([string]$Path) {
    $findings = @()
    Get-ChildItem -LiteralPath $Path -File -Recurse | Where-Object {
        $_.FullName -notmatch '[\\/](?:\.git|node_modules|bin|obj|__pycache__|captures|artifacts)[\\/]' -and
        $extensions -contains $_.Extension
    } | ForEach-Object {
        $relative = $_.FullName.Substring($Path.Length).TrimStart('\', '/')
        if ($excludedRelative -contains $relative) { return }
        $text = Get-Content -LiteralPath $_.FullName -Raw
        foreach ($rule in @(
            @{ name = 'absolute-work-root'; pattern = 'C:[\\/]work[\\/]' },
            @{ name = 'baseline-source-link'; pattern = 'djcdevelopment/baseline/(?:blob|tree)/(?:main/(?:Lumberjacks|infra/gcp/p7|fieldlab|tools/(?:p7|wave0|workbench|authority-lab|guest-package)))' },
            @{ name = 'missing-monorepo-tree'; pattern = '(?:Join-Path|context:)\s*[^\r\n]*(?:network[\\/](?:mod|mcp)|tools[\\/]i5)' }
        )) {
            if ($text -match $rule.pattern) {
                $findings += [pscustomobject]@{ File = $relative; Rule = $rule.name }
            }
        }
    }
    return @($findings)
}

$findings = Test-Tree $rootPath
if ($findings.Count -gt 0) {
    $findings | Format-Table -AutoSize | Out-String | Write-Error
    exit 1
}

if ($SelfTest) {
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('lumberjacks-g1-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        [IO.File]::WriteAllText((Join-Path $temporary 'bad.ps1'), '$x = ''C:\work\sibling''')
        if ((Test-Tree $temporary).Count -eq 0) { throw 'G1 self-test did not reject its bad fixture' }
    }
    finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
}

[pscustomobject]@{ Schema = 'lumberjacks-boundary-guard/v1'; Guard = 'G1'; Verdict = 'passed'; SelfTest = [bool]$SelfTest } |
    ConvertTo-Json -Compress
