param(
  [Parameter(Mandatory = $true)]
  [string]$Id,

  [ValidateSet("done", "failed", "blocked", "cancelled")]
  [string]$Status = "done",

  [string]$Repo = "C:\work\comfy",
  [string]$OutDir = "C:\work\comfy\fieldlab\runs\build-requests",
  [string]$Summary = "",
  [string]$LogPath = "",
  [string[]]$Commits = @()
)

$ErrorActionPreference = "Stop"

$receiptPath = Join-Path $OutDir "$Id.receipt.json"
$ledgerPath = Join-Path $OutDir "ledger.jsonl"

if (-not (Test-Path -LiteralPath $receiptPath)) {
  throw "Receipt not found: $receiptPath"
}

$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
$now = (Get-Date).ToUniversalTime().ToString("o")
$repoHead = git -C $Repo rev-parse HEAD 2>$null
$repoBranch = git -C $Repo branch --show-current 2>$null
$repoStatus = git -C $Repo status --short 2>$null

$receipt.status = $Status
$receipt.updated_utc = $now
$receipt.repo.branch = $repoBranch
$receipt.repo.head = $repoHead
$receipt.repo.status_short = @($repoStatus)
$receipt.result = [ordered]@{
  summary = $Summary
  log_path = $LogPath
  commits = @($Commits)
}

$json = $receipt | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $receiptPath -Value $json -Encoding UTF8
Add-Content -LiteralPath $ledgerPath -Value ($json -replace "`r?`n", "") -Encoding UTF8

Write-Host "BUILD_REQUEST_RECEIPT=$Id" -ForegroundColor Green
Write-Host "STATUS=$Status"
Write-Host "RECEIPT=$receiptPath"
