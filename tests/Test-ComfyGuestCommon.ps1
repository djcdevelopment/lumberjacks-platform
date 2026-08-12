[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repoRoot 'tools\guest-package\lib\ComfyGuestCommon.psm1') -Force

# Exact shape observed on the hosted PowerShell 7 runner: an original section,
# a later installer-managed section, and a final user-owned section.
$config = @'
[General]
foo=bar

[Lumberjacks]
old=value

[Automation]
sentinel=true


[Lumberjacks]
lumberjacksEnrollmentId=e
zdoAuthoritativeConsumerEnabled=true
lumberjacksAuthoritativeWindowId=w
lumberjacksGatewayUrl=http://127.0.0.1
lumberjacksClientAccessKey=fixture-access

[General]
userEdited=true

[Lumberjacks]
userOwnedKey=keep
'@
$managedKeys = @(
    'lumberjacksGatewayUrl',
    'lumberjacksAuthoritativeWindowId',
    'lumberjacksEnrollmentId',
    'lumberjacksClientAccessKey',
    'zdoAuthoritativeConsumerEnabled'
)

$cleaned = Remove-ComfyBepInExKeys -Text $config -Keys $managedKeys
foreach ($key in $managedKeys) {
    if ($cleaned -match ('(?m)^\s*' + [regex]::Escape($key) + '\s*=')) {
        throw "Managed key survived repeated-section cleanup: $key"
    }
}
foreach ($expected in @('old=value', 'sentinel=true', 'userEdited=true', 'userOwnedKey=keep')) {
    if (-not $cleaned.Contains($expected)) {
        throw "User-owned configuration was lost: $expected"
    }
}

[pscustomobject]@{
    Schema = 'comfy-guest-common-regression/v1'
    ManagedKeysRemoved = $managedKeys.Count
    UserValuesPreserved = 4
    Verdict = 'passed'
} | ConvertTo-Json -Compress
