#Requires -Version 5.1
<#
.SYNOPSIS
Create one bounded manifest for the physical-client native cutover driver.

.DESCRIPTION
The manifest is data, not an input macro. ComfyNetworkSense accepts only the fixed action kinds
declared here, validates every bound again in-process, and correlates the file to an expiring
native-autotest request.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunId,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [ValidateSet('baseline', 'full')]
    [string] $Profile = 'baseline',

    [string] $OmenOwnershipTargetTag = '',

    [string] $I5OwnershipTargetTag = ''
)

$ErrorActionPreference = 'Stop'

if ($RunId.Length -gt 80 -or $RunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw "RunId must be an 80-character-or-shorter safe token: $RunId"
}
if ($Profile -eq 'full') {
    $expectedPrefix = "cutover-$RunId"
    foreach ($tag in @($OmenOwnershipTargetTag, $I5OwnershipTargetTag)) {
        if ([string]::IsNullOrWhiteSpace($tag) -or
            -not $tag.StartsWith($expectedPrefix, [StringComparison]::Ordinal) -or
            $tag.Length -gt 96 -or $tag -notmatch '^[A-Za-z0-9._-]+$') {
            throw "Full profile ownership tags must begin with '$expectedPrefix' and be safe tokens."
        }
    }
}

function New-Action(
    [string] $Id,
    [string] $Client,
    [string] $Kind,
    [double] $DeadlineSeconds,
    [double] $DurationSeconds = 0,
    [double] $DistanceMeters = 0,
    [double] $RadiusMeters = 0,
    [string] $Direction = '',
    [string] $TargetTag = '') {
    [ordered]@{
        id = $Id
        client = $Client
        kind = $Kind
        deadline_seconds = $DeadlineSeconds
        duration_seconds = $DurationSeconds
        distance_meters = $DistanceMeters
        radius_meters = $RadiusMeters
        direction = $Direction
        target_tag = $TargetTag
    }
}

$actions = @(
    New-Action 'omen-settle' 'omen' 'wait' 10 2
    New-Action 'i5-settle' 'i5' 'wait' 10 2
    New-Action 'omen-move' 'omen' 'move' 15 3 6 0 east
    New-Action 'i5-move' 'i5' 'move' 15 3 6 0 north
)

if ($Profile -eq 'full') {
    $actions += @(
        New-Action 'omen-pickup' 'omen' 'pickup_nearest' 25 0 0 8
        New-Action 'i5-pickup' 'i5' 'pickup_nearest' 25 0 0 8
        New-Action 'omen-ownership' 'omen' 'ownership_target' 25 0 0 16 '' $OmenOwnershipTargetTag
        New-Action 'i5-ownership' 'i5' 'ownership_target' 25 0 0 16 '' $I5OwnershipTargetTag
        New-Action 'omen-zone' 'omen' 'zone_cross' 20 0 72 0 east
        New-Action 'i5-zone' 'i5' 'zone_cross' 20 0 72 0 west
    )
}

$actions += @(
    New-Action 'omen-disconnect-resume' 'omen' 'disconnect_resume' 15
    New-Action 'i5-disconnect-resume' 'i5' 'disconnect_resume' 15
    New-Action 'omen-resumed' 'omen' 'wait' 10 2
    New-Action 'i5-resumed' 'i5' 'wait' 10 2
)

$manifest = [ordered]@{
    schema_version = 1
    run_id = $RunId
    created_utc = [DateTimeOffset]::UtcNow.ToString('o')
    expires_utc = [DateTimeOffset]::UtcNow.AddHours(1).ToString('o')
    profile = $Profile
    actions = $actions
}

$absoluteOutput = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $absoluteOutput
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText(
    $absoluteOutput,
    $json + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))

[ordered]@{
    schema_version = 1
    receipt_type = 'native_cutover_scenario_manifest'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    run_id = $RunId
    profile = $Profile
    action_count = $actions.Count
    path = $absoluteOutput
    sha256 = (Get-FileHash -LiteralPath $absoluteOutput -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 6
