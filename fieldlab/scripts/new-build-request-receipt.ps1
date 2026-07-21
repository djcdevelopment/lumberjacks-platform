[CmdletBinding()]
param(
  [Parameter(ValueFromPipeline = $true)]
  [string]$RequestBody,

  [Parameter(Mandatory = $true)]
  [string]$Title,

  [string]$Lane = "hearth",
  [string]$Repo = "C:\work\comfy",
  [string]$Target = "comfy-lumberjacks-p7",
  [string]$OutDir = "C:\work\comfy\fieldlab\runs\build-requests",
  [switch]$PrintPrompt
)

$ErrorActionPreference = "Stop"

$body = if (-not [string]::IsNullOrWhiteSpace($RequestBody)) {
  $RequestBody
} elseif ($MyInvocation.ExpectingInput) {
  @($input) -join [Environment]::NewLine
} else {
  [Console]::In.ReadToEnd()
}

if ([string]::IsNullOrWhiteSpace($body)) {
  throw "Pipe a request body into this script, or provide stdin text."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$now = Get-Date
$stamp = $now.ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$id = "br-$stamp-$suffix"
$requestPath = Join-Path $OutDir "$id.request.md"
$receiptPath = Join-Path $OutDir "$id.receipt.json"
$ledgerPath = Join-Path $OutDir "ledger.jsonl"

$repoHead = git -C $Repo rev-parse HEAD 2>$null
$repoBranch = git -C $Repo branch --show-current 2>$null
$repoStatus = git -C $Repo status --short 2>$null

$requestText = @"
# $Title

Receipt: $id
Lane: $Lane
Target: $Target
Repo: $Repo
Branch: $repoBranch
Head: $repoHead
Created UTC: $($now.ToUniversalTime().ToString("o"))

## Request

$body
"@

Set-Content -LiteralPath $requestPath -Value $requestText -Encoding UTF8

$receipt = [ordered]@{
  schema_version = 1
  id = $id
  title = $Title
  lane = $Lane
  target = $Target
  status = "created"
  created_utc = $now.ToUniversalTime().ToString("o")
  updated_utc = $now.ToUniversalTime().ToString("o")
  repo = [ordered]@{
    path = $Repo
    branch = $repoBranch
    head = $repoHead
    status_short = @($repoStatus)
  }
  request_path = $requestPath
  result = $null
}

$json = $receipt | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $receiptPath -Value $json -Encoding UTF8
Add-Content -LiteralPath $ledgerPath -Value ($json -replace "`r?`n", "") -Encoding UTF8

Write-Host "BUILD_REQUEST_RECEIPT=$id" -ForegroundColor Green
Write-Host "REQUEST=$requestPath"
Write-Host "RECEIPT=$receiptPath"

if ($PrintPrompt) {
  Write-Host ""
  Write-Host "----- prompt -----" -ForegroundColor Cyan
  Get-Content -LiteralPath $requestPath
}
