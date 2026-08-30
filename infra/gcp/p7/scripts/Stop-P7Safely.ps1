#Requires -Version 5.1
<#
.SYNOPSIS
Save CreatorOS/Valheim, verify the shutdown receipt, then stop the P7 VM.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $SshTarget = 'comfy-p7',
    [string] $Instance = 'comfy-lumberjacks-p7',
    [string] $Zone = 'us-west1-b',
    [string] $Project = 'lumberjacks-exp-20260711-djc'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1')
Assert-RepoIdentity -RepoRoot $repoRoot | Out-Null
if (-not $PSCmdlet.ShouldProcess("$Project/$Zone/$Instance", 'save the selected Valheim world and stop the GCP VM')) {
    return
}

$remote = @'
set -eu
sudo systemctl stop comfy-lumberjacks-p7.service
latest=$(sudo find /mnt/comfy-p7/evidence/shutdown -maxdepth 1 -type f -name '*.json' -printf '%T@ %p\n' | sort -nr | head -1 | cut -d' ' -f2-)
test -n "$latest"
sudo jq -c '{schema,status,detail,completed_utc,world_name,was_running,world_saved_log_seen,db_bytes,fwl_bytes,db_sha256,fwl_sha256,interrupted_temp_files,stack_stop_exit}' "$latest"
'@
$lines = & ssh -o BatchMode=yes -o ConnectTimeout=8 $SshTarget $remote
if ($LASTEXITCODE -ne 0) { throw 'P7 stack did not produce a passing graceful-shutdown receipt; the VM remains on.' }
$receipt = ($lines | Select-Object -Last 1) | ConvertFrom-Json
if ([string]$receipt.schema -ne 'comfy-p7-graceful-shutdown/v1' -or
    [string]$receipt.status -ne 'passed' -or
    [long]$receipt.db_bytes -le 0 -or [long]$receipt.fwl_bytes -le 0 -or
    [string]$receipt.db_sha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]$receipt.fwl_sha256 -notmatch '^[0-9a-f]{64}$' -or
    [int]$receipt.interrupted_temp_files -ne 0 -or [int]$receipt.stack_stop_exit -ne 0) {
    throw 'P7 graceful-shutdown receipt failed validation; the VM remains on.'
}

& gcloud compute instances stop $Instance --zone $Zone --project $Project --quiet
if ($LASTEXITCODE -ne 0) { throw 'The stack saved cleanly, but gcloud did not stop the P7 VM.' }
[pscustomobject]@{
    instance = $Instance
    world = [string]$receipt.world_name
    saved = [bool]$receipt.world_saved_log_seen
    db_bytes = [long]$receipt.db_bytes
    fwl_bytes = [long]$receipt.fwl_bytes
    db_sha256 = [string]$receipt.db_sha256
    fwl_sha256 = [string]$receipt.fwl_sha256
    stopped = $true
}
