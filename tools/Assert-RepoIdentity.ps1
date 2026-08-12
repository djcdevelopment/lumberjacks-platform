# Stale-checkout-root defense. Dot-source this file, then call
# Assert-RepoIdentity before a script performs state-changing work.
function Assert-RepoIdentity {
    [CmdletBinding()]
    param(
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
        [string]$Expected = 'djcdevelopment/lumberjacks-platform'
    )

    $resolvedRoot = [IO.Path]::GetFullPath($RepoRoot)
    $origin = (& git -C $resolvedRoot remote get-url origin 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($origin)) {
        throw "REPO IDENTITY FAILURE: no origin at '$resolvedRoot'; refusing to act."
    }
    if ($origin -notmatch [regex]::Escape($Expected)) {
        throw "REPO IDENTITY FAILURE: origin '$origin' does not match '$Expected'; refusing to act."
    }
    return [pscustomobject]@{
        Schema = 'comfy-repo-identity/v1'
        Repository = $Expected
        Root = $resolvedRoot
        Origin = $origin
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Assert-RepoIdentity | ConvertTo-Json -Compress
}
