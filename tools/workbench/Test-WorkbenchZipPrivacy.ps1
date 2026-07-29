<#
.SYNOPSIS
Deny-list privacy scanner that gates Community Workbench tool zips before publish.
Windows PowerShell 5.1 compatible ONLY.

.DESCRIPTION
Scans a directory (or a .zip, extracted to a temp dir) for community-privacy leaks:
real player handles pulled live from waypoints.json, forbidden filenames (real guild
trackers, an unsanitized quest-picker.html, the real waypoints.json), tailnet/hostname
leakage, SteamID64s, non-default service credentials, absolute local paths that expose
machine layout, and the P7 GCP VM's external IPv4 (looked up from the infra docs, not
hardcoded).

Every file is scanned two ways: its filename always, and its content when the
extension is on the text allowlist. Findings are printed with file, rule, scan type,
and the matched text truncated to 60 characters.

.PARAMETER Path
A .zip file or a directory to scan.

.PARAMETER SelfTest
Builds a clean fixture and a poisoned fixture (one violation per rule) under the
session scratchpad, runs both through the scanner, asserts the clean fixture is
spotless and the poisoned fixture trips every rule, and prints PASS/FAIL per rule.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File Test-WorkbenchZipPrivacy.ps1 -Path C:\staging\quest-picker
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File Test-WorkbenchZipPrivacy.ps1 -SelfTest

.NOTES
Exit codes: 0 = clean, 1 = findings (or self-test failure), 2 = usage error.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path,

    [switch]$SelfTest
)

# ----------------------------------------------------------------------------
# Constants
# ----------------------------------------------------------------------------

$Script:RepoRoot = 'C:\work\baseline'
$Script:ScratchpadRoot = 'C:\Users\derek\AppData\Local\Temp\claude\C--work-baseline\a2bacb2d-1122-4c2c-8b49-4e6ea15132e6\scratchpad'

# Text files get a full content scan on top of the always-on filename scan.
$Script:TextExtensions = @(
    '.md', '.txt', '.json', '.jsonl', '.xml', '.html', '.htm', '.css', '.js', '.mjs',
    '.ps1', '.psm1', '.cmd', '.bat', '.cfg', '.config', '.cs', '.py', '.yml', '.yaml',
    '.csv', '.tsv'
)

# Skip content-scanning anything absurdly large that slipped in with an allowlisted
# extension; the filename scan still runs regardless.
$Script:ContentScanSizeCapBytes = 25MB

# ----------------------------------------------------------------------------
# Small I/O helpers
#
# Windows PowerShell 5.1's Get-Content/Set-Content default to the system ANSI
# codepage when a file has no BOM, silently mangling non-ASCII text (verified
# against waypoints.json's real owner names, which include stylized Unicode
# modifier-letter characters). Route every meaningful text read/write through
# .NET's File.ReadAllText/WriteAllText instead, which default to UTF-8.
# ----------------------------------------------------------------------------

function Read-TextFileUtf8 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.File]::ReadAllText($Path)
}

function Write-TextFileUtf8 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )
    $noBomUtf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $noBomUtf8)
}

# ----------------------------------------------------------------------------
# Finding construction
# ----------------------------------------------------------------------------

function New-Finding {
    param(
        [string]$File,
        [string]$Rule,
        [string]$ScanType,
        [string]$Match
    )
    $m = $Match
    if ($null -eq $m) { $m = '' }
    if ($m.Length -gt 60) { $m = $m.Substring(0, 60) }
    return [PSCustomObject]@{
        File     = $File
        Rule     = $Rule
        ScanType = $ScanType
        Match    = $m
    }
}

# "Word-ish boundary": require a non-alphanumeric/underscore (or string edge) on
# each side of the literal. Deliberately ASCII-only rather than regex \b, because
# \b's Unicode word-char behavior is not something to bet correctness on when real
# player handles contain stylized Unicode modifier letters (e.g. "Jordsstaff"-style
# superscript decorations) - an approximate, predictable boundary is what the spec
# asks for ("word-ish"), and this sidesteps the Unicode-category question entirely.
function New-WordishPattern {
    param([Parameter(Mandatory = $true)][string]$Literal)
    $escaped = [regex]::Escape($Literal)
    return "(?<![A-Za-z0-9_])$escaped(?![A-Za-z0-9_])"
}

# ----------------------------------------------------------------------------
# Rule (g): P7 GCP external IPv4 - derived at runtime from the infra docs rather
# than hardcoded, so it self-corrects if the VM is ever re-provisioned with a new
# address (the docs would be updated too). Only trusted when it is the sole,
# unambiguous public candidate across both files.
#
# Authoring-time verification (2026-07-28): infra/gcp/p7/VOLUNTEER-ENDPOINT.md and
# infra/gcp/p7/README.md both plainly and repeatedly state the reserved external
# address as 8.231.129.249 (google_compute_address.p7 / terraform output public_ip),
# e.g. "Valheim join: 8.231.129.249:2456" and "Player Gateway: http://8.231.129.249
# :42317" - 18 occurrences across the two docs, the only public-IP candidate found.
# Rule (g) is ACTIVE.
# ----------------------------------------------------------------------------

function Test-IsPrivateIPv4 {
    param([Parameter(Mandatory = $true)][string]$Ip)
    $parts = $Ip.Split('.')
    $o0 = [int]$parts[0]
    $o1 = [int]$parts[1]
    if ($o0 -eq 10) { return $true }
    if ($o0 -eq 172 -and $o1 -ge 16 -and $o1 -le 31) { return $true }
    if ($o0 -eq 192 -and $o1 -eq 168) { return $true }
    if ($o0 -eq 169 -and $o1 -eq 254) { return $true }
    if ($o0 -eq 100 -and $o1 -ge 64 -and $o1 -le 127) { return $true }
    return $false
}

function Get-GcpExternalIp {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $docs = @(
        (Join-Path $RepoRoot 'infra\gcp\p7\VOLUNTEER-ENDPOINT.md'),
        (Join-Path $RepoRoot 'infra\gcp\p7\README.md')
    )

    # Known non-external addresses seen in these docs: loopback, "any" CIDR base,
    # and the two public DNS resolvers used only to verify the DNS record.
    $excluded = @('0.0.0.0', '127.0.0.1', '1.1.1.1', '8.8.8.8', '8.8.4.4', '1.0.0.1', '255.255.255.255')
    $ipRegex = '\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b'

    $counts = @{}
    $anyDocFound = $false
    foreach ($doc in $docs) {
        if (-not (Test-Path -LiteralPath $doc)) { continue }
        $anyDocFound = $true
        $text = $null
        try {
            $text = Read-TextFileUtf8 -Path $doc
        } catch {
            Write-Warning ("Could not read {0}: {1}" -f $doc, $_.Exception.Message)
        }
        if ($null -eq $text) { continue }
        $ms = [regex]::Matches($text, $ipRegex)
        foreach ($m in $ms) {
            $ipStr = $m.Value
            if ($excluded -contains $ipStr) { continue }
            if (Test-IsPrivateIPv4 -Ip $ipStr) { continue }
            if (-not $counts.ContainsKey($ipStr)) { $counts[$ipStr] = 0 }
            $counts[$ipStr] = $counts[$ipStr] + 1
        }
    }

    if (-not $anyDocFound) {
        Write-Warning ("Rule (g) inactive: neither source doc was found under {0}." -f $RepoRoot)
        return $null
    }
    if ($counts.Count -eq 0) {
        Write-Warning 'Rule (g) inactive: no plainly-stated external IPv4 found in the source docs.'
        return $null
    }

    $sorted = @($counts.GetEnumerator() | Sort-Object -Property Value -Descending)
    $top = $sorted[0]
    if ($sorted.Count -gt 1 -and $sorted[1].Value -eq $top.Value) {
        $candidates = ($sorted | ForEach-Object { $_.Key }) -join ', '
        Write-Warning ("Rule (g) inactive: ambiguous external IPv4 candidates with equal confidence: {0}" -f $candidates)
        return $null
    }

    return $top.Key
}

# ----------------------------------------------------------------------------
# Build the full rule set (static rules + dynamic rule (a) player handles + rule
# (g) GCP IP, both re-derived at runtime from the live source-of-truth files).
# ----------------------------------------------------------------------------

function Get-ScanRules {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $rules = @()
    $ownerRaw = @()

    $waypointsPath = Join-Path $RepoRoot 'waypoints.json'
    if (Test-Path -LiteralPath $waypointsPath) {
        try {
            $wpText = Read-TextFileUtf8 -Path $waypointsPath
            $wp = $wpText | ConvertFrom-Json
            $ownerRaw = @(
                $wp.waypoints |
                    ForEach-Object { $_.owner } |
                    Where-Object { $_ -and ($_.Trim().Length -gt 0) } |
                    Select-Object -Unique
            )
        } catch {
            Write-Warning ("Failed to parse waypoints.json at {0}: {1}" -f $waypointsPath, $_.Exception.Message)
        }
    } else {
        Write-Warning ("waypoints.json not found at {0} - rule (a) real-player-handle checks are DISABLED for this run." -f $waypointsPath)
    }

    foreach ($owner in $ownerRaw) {
        $pattern = '(?i)' + (New-WordishPattern -Literal $owner)
        $rules += [PSCustomObject]@{ Id = 'a.player-handle'; Pattern = $pattern; AllowedValues = @() }
    }

    # c) tailnet FQDN + the one literal internal hostname. comfy-p7 is deliberately
    # NOT in this list - it is public DNS (comfy-p7.duckdns.org) and allowed.
    $rules += [PSCustomObject]@{
        Id = 'c.tailnet-fqdn'; Pattern = '(?i)\b[a-z0-9-]+\.tail[0-9a-f]{4,}\.ts\.net\b'; AllowedValues = @()
    }
    $rules += [PSCustomObject]@{
        Id = 'c.hostname-i5laptop'; Pattern = '(?i)' + (New-WordishPattern -Literal 'i5-laptop'); AllowedValues = @()
    }

    # d) SteamID64
    $rules += [PSCustomObject]@{ Id = 'd.steamid64'; Pattern = '\b7656119\d{10}\b'; AllowedValues = @() }

    # e) credentials: known-default header values are allowed; the generic
    # api-key/secret/password/token literal has no allowance.
    $rules += [PSCustomObject]@{
        Id            = 'e.cred-header'
        Pattern       = '(?i)(?:x-hearth-key|x-comfy-key|x-lumberjacks-telemetry-key)\s*[:=]\s*(?<value>\S+)'
        AllowedValues = @('comfy-dev-local')
    }
    $rules += [PSCustomObject]@{
        Id            = 'e.cred-generic'
        Pattern       = '(?i)\b(api[_-]?key|secret|password|token)\s*[:=]\s*[''"][A-Za-z0-9+/_-]{16,}[''"]'
        AllowedValues = @()
    }

    # f) absolute local paths that leak machine layout
    $rules += [PSCustomObject]@{ Id = 'f.path-users-derek'; Pattern = '(?i)C:\\Users\\derek'; AllowedValues = @() }
    $rules += [PSCustomObject]@{ Id = 'f.path-commandcenter'; Pattern = '(?i)C:\\work\\commandcenter'; AllowedValues = @() }

    # g) GCP external IP - only added when unambiguously found (see Get-GcpExternalIp)
    $gcpIp = Get-GcpExternalIp -RepoRoot $RepoRoot
    if ($gcpIp) {
        $ipPattern = '(?<![\d.])' + [regex]::Escape($gcpIp) + '(?![\d.])'
        $rules += [PSCustomObject]@{ Id = 'g.gcp-external-ip'; Pattern = $ipPattern; AllowedValues = @() }
    }

    return [PSCustomObject]@{
        Rules         = $rules
        OwnerCount    = $ownerRaw.Count
        OwnerRawNames = $ownerRaw
        GcpIp         = $gcpIp
    }
}

# ----------------------------------------------------------------------------
# Generic pattern-rule matcher - applied to both the filename string and (when
# the extension qualifies) the file's full text content.
# ----------------------------------------------------------------------------

function Invoke-RuleScan {
    param(
        [string]$Text,
        [string]$RelativePath,
        [string]$ScanType,
        [System.Object[]]$Rules
    )
    $results = @()
    if ([string]::IsNullOrEmpty($Text)) { return $results }

    $trimChars = [char[]]@('"', "'", ',', ';', '.', '!', '?', ')', '(')

    foreach ($rule in $Rules) {
        $regexMatches = [regex]::Matches($Text, $rule.Pattern)
        foreach ($m in $regexMatches) {
            $skip = $false
            if ($rule.AllowedValues.Count -gt 0) {
                $valGroup = $m.Groups['value']
                if ($valGroup -and $valGroup.Success) {
                    $val = $valGroup.Value.Trim()
                    $val = $val.Trim($trimChars)
                    foreach ($allowed in $rule.AllowedValues) {
                        if ($val -ieq $allowed) { $skip = $true }
                    }
                }
            }
            if (-not $skip) {
                $results += New-Finding -File $RelativePath -Rule $rule.Id -ScanType $ScanType -Match $m.Value
            }
        }
    }
    return $results
}

# ----------------------------------------------------------------------------
# Rule (b): forbidden files by name/identity. Structural, not a generic regex
# match, so it lives outside the pattern-rule table.
#
# DESIGN DEVIATION from the literal spec: an xlsx matching *guild*tracker*/*guild*
# is NOT flagged when its name also contains "sample". The repo already ships a
# legitimate synthetic fixture, tools\workbench\samples\quest-picker\
# make_sample_tracker.py, whose documented output is "sample-guild-tracker.xlsx"
# ("every name here is invented") and which New-WorkbenchZip.ps1 stages into every
# quest-picker zip. A literal "any *.xlsx with guild in the name" rule would
# permanently fail that tool's mandatory privacy gate. The real files this rule
# exists to catch - data\raw\ranger-guild-tracker.xlsx and
# data\raw\slayer-guild-tracker.xlsx - do not contain "sample" and are still
# caught. This mirrors the "sample" escape hatch the spec already uses for
# waypoints.sample.json.
# ----------------------------------------------------------------------------

function Test-ForbiddenFileName {
    param($FileInfo, [string]$RelativePath)

    $results = @()
    $name = $FileInfo.Name

    if ($name -like '*.xlsx') {
        $matchesGuild = ($name -like '*guild*tracker*') -or ($name -like '*guild*')
        $isSample = ($name -like '*sample*')
        if ($matchesGuild -and (-not $isSample)) {
            $results += New-Finding -File $RelativePath -Rule 'b.xlsx-guild' -ScanType 'filename' -Match $name
        }
    }

    if ($name -ieq 'quest-picker.html') {
        $hasMarker = $false
        try {
            $text = Read-TextFileUtf8 -Path $FileInfo.FullName
            if ($text -match 'SAMPLE-CATALOG') { $hasMarker = $true }
        } catch {
            Write-Warning ("Could not read {0} to check for the SAMPLE-CATALOG marker: {1}" -f $RelativePath, $_.Exception.Message)
        }
        if (-not $hasMarker) {
            $results += New-Finding -File $RelativePath -Rule 'b.quest-picker' -ScanType 'filename' -Match $name
        }
    }

    if ($name -ieq 'waypoints.json') {
        $results += New-Finding -File $RelativePath -Rule 'b.waypoints-real' -ScanType 'filename' -Match $name
    }

    return $results
}

# ----------------------------------------------------------------------------
# Filesystem walk
# ----------------------------------------------------------------------------

function Get-RelativePath {
    param([string]$Root, [string]$FullPath)
    $rootFull = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('\', '/')
    if ($FullPath.Length -gt $rootFull.Length -and $FullPath.Substring(0, $rootFull.Length) -ieq $rootFull) {
        $rel = $FullPath.Substring($rootFull.Length).TrimStart('\', '/')
    } else {
        $rel = Split-Path -Leaf $FullPath
    }
    if ([string]::IsNullOrEmpty($rel)) { $rel = Split-Path -Leaf $FullPath }
    return $rel
}

function Invoke-FileScan {
    param($FileInfo, [string]$ScanRoot, [System.Object[]]$Rules, [string[]]$TextExtensions)

    $relative = Get-RelativePath -Root $ScanRoot -FullPath $FileInfo.FullName
    $results = @()

    # Filename scan: every file, regardless of extension.
    $results += Invoke-RuleScan -Text $FileInfo.Name -RelativePath $relative -ScanType 'filename' -Rules $Rules
    $results += Test-ForbiddenFileName -FileInfo $FileInfo -RelativePath $relative

    # Content scan: only allowlisted text extensions.
    $ext = $FileInfo.Extension.ToLowerInvariant()
    if ($TextExtensions -contains $ext) {
        if ($FileInfo.Length -le $Script:ContentScanSizeCapBytes) {
            $text = $null
            try {
                $text = Read-TextFileUtf8 -Path $FileInfo.FullName
            } catch {
                Write-Warning ("Could not read {0} for content scan: {1}" -f $relative, $_.Exception.Message)
            }
            if ($null -ne $text) {
                $results += Invoke-RuleScan -Text $text -RelativePath $relative -ScanType 'content' -Rules $Rules
            }
        } else {
            Write-Warning ("Skipping content scan for {0} (exceeds size guard)" -f $relative)
        }
    }

    return $results
}

function Invoke-PrivacyScan {
    param([string]$ScanRoot, [System.Object[]]$Rules, [string[]]$TextExtensions)
    $results = @()
    $files = Get-ChildItem -LiteralPath $ScanRoot -Recurse -File -Force
    foreach ($f in $files) {
        $results += Invoke-FileScan -FileInfo $f -ScanRoot $ScanRoot -Rules $Rules -TextExtensions $TextExtensions
    }
    return $results
}

# ----------------------------------------------------------------------------
# Zip handling
# ----------------------------------------------------------------------------

function Expand-ZipToTemp {
    param([Parameter(Mandatory = $true)][string]$ZipPath)
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('wbzscan_' + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $tempDir -Force
    return $tempDir
}

# Scans a .zip file OR a directory. Returns @{ Findings = <array>; ErrorMessage = <string-or-null> }.
function Invoke-PathScan {
    param([string]$InputPath, [System.Object[]]$Rules, [string[]]$TextExtensions)

    $tempDir = $null
    $findings = @()
    $errorMessage = $null
    try {
        if (-not (Test-Path -LiteralPath $InputPath)) {
            $errorMessage = "Path not found: $InputPath"
        } else {
            $item = Get-Item -LiteralPath $InputPath
            if ($item.PSIsContainer) {
                $findings = @(Invoke-PrivacyScan -ScanRoot $item.FullName -Rules $Rules -TextExtensions $TextExtensions)
            } elseif ($item.Extension -ieq '.zip') {
                $tempDir = Expand-ZipToTemp -ZipPath $item.FullName
                $findings = @(Invoke-PrivacyScan -ScanRoot $tempDir -Rules $Rules -TextExtensions $TextExtensions)
            } else {
                $errorMessage = "Path must be a .zip file or a directory: $InputPath"
            }
        }
    } catch {
        $errorMessage = $_.Exception.Message
    } finally {
        if ($tempDir -and (Test-Path -LiteralPath $tempDir)) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    return [PSCustomObject]@{ Findings = $findings; ErrorMessage = $errorMessage }
}

# ----------------------------------------------------------------------------
# Human-readable report. Uses Write-Host deliberately (not Write-Output) so this
# never pollutes a caller's captured return value.
# ----------------------------------------------------------------------------

function Write-ScanReport {
    param([System.Object[]]$Findings, [string]$SourcePath, $Context)

    $ruleDescriptions = @{
        'a.player-handle'      = 'Real community player handle (waypoints.json owners)'
        'b.xlsx-guild'         = 'Forbidden filename: guild-tracker spreadsheet'
        'b.quest-picker'       = 'quest-picker.html missing the SAMPLE-CATALOG marker'
        'b.waypoints-real'     = 'Real waypoints.json shipped (use waypoints.sample.json)'
        'c.tailnet-fqdn'       = 'Tailscale/tailnet hostname'
        'c.hostname-i5laptop'  = 'Internal hostname literal (i5-laptop)'
        'd.steamid64'          = 'SteamID64'
        'e.cred-header'        = 'Non-default service credential header value'
        'e.cred-generic'       = 'Generic secret/password/token/api-key literal'
        'f.path-users-derek'   = 'Absolute path leaking C:\Users\derek'
        'f.path-commandcenter' = 'Absolute path leaking C:\work\commandcenter'
        'g.gcp-external-ip'    = "P7 GCP VM external IPv4 address"
    }

    Write-Host "Test-WorkbenchZipPrivacy scan of: $SourcePath"
    Write-Host ("Player-handle deny list : {0} real name(s) loaded from waypoints.json" -f $Context.OwnerCount)
    if ($Context.GcpIp) {
        Write-Host ("Rule (g) GCP external IP: ACTIVE - flagging literal {0}" -f $Context.GcpIp)
    } else {
        Write-Host 'Rule (g) GCP external IP: INACTIVE - no unambiguous external IPv4 found in source docs'
    }
    Write-Host ''

    $findingsArr = @($Findings)
    if ($findingsArr.Count -eq 0) {
        Write-Host 'CLEAN - no privacy findings.'
        return
    }

    Write-Host ("FINDINGS ({0}):" -f $findingsArr.Count)
    foreach ($f in ($findingsArr | Sort-Object File, Rule)) {
        $desc = $ruleDescriptions[$f.Rule]
        if (-not $desc) { $desc = $f.Rule }
        Write-Host ("  [{0}] {1}" -f $f.Rule, $desc)
        Write-Host ("      file : {0}" -f $f.File)
        Write-Host ("      scan : {0}" -f $f.ScanType)
        Write-Host ("      match: {0}" -f $f.Match)
    }

    $uniqueFiles = @($findingsArr | Select-Object -ExpandProperty File -Unique)
    Write-Host ''
    Write-Host ("SUMMARY: {0} finding(s) across {1} file(s). NOT CLEAN - do not publish." -f $findingsArr.Count, $uniqueFiles.Count)
}

# ----------------------------------------------------------------------------
# Self-test fixtures
# ----------------------------------------------------------------------------

function New-CleanFixture {
    param([Parameter(Mandatory = $true)][string]$Dir)

    New-Item -ItemType Directory -Path $Dir -Force | Out-Null

    Write-TextFileUtf8 -Path (Join-Path $Dir 'readme.md') -Content (
        "This is a benign placeholder file for the privacy-scanner self-test clean fixture.`n" +
        "It references the public endpoint comfy-p7.duckdns.org and the bare host comfy-p7,`n" +
        "both of which are allowed. No secrets, no real player handles, no machine paths."
    )

    Write-TextFileUtf8 -Path (Join-Path $Dir 'config.cfg') -Content (
        "x-hearth-key: comfy-dev-local`n" +
        "note: this is the known public dev default and must NOT be flagged`n"
    )

    Write-TextFileUtf8 -Path (Join-Path $Dir 'waypoints.sample.json') -Content (
        '{"waypoints":[{"owner":"SampleHero","label":"demo-only"}]}'
    )

    Write-TextFileUtf8 -Path (Join-Path $Dir 'quest-picker.html') -Content (
        "<html><body>Sample picker.</body></html>`n" +
        "<!-- SAMPLE-CATALOG: synthetic demonstration data only -->`n"
    )

    Write-TextFileUtf8 -Path (Join-Path $Dir 'loot-log.xlsx') -Content 'placeholder bytes, filename has no guild in it'
    Write-TextFileUtf8 -Path (Join-Path $Dir 'sample-guild-tracker.xlsx') -Content 'placeholder synthetic sample tracker bytes'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'notes.txt') -Content (
        "Generic build notes. Internal IPs like 10.0.0.5 and 192.168.1.10 are private and fine.`n" +
        "No SteamID, no credentials, no tailnet hosts here."
    )

    Write-TextFileUtf8 -Path (Join-Path $Dir 'data.csv') -Content "col1,col2`nval1,val2`n"
}

function New-PoisonedFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,
        [string]$SampleOwner,
        [string]$GcpIp
    )

    New-Item -ItemType Directory -Path $Dir -Force | Out-Null

    if ($SampleOwner) {
        Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-a-player-handle.md') `
            -Content "Base owner: $SampleOwner built this dock and it should never ship."
    }

    Write-TextFileUtf8 -Path (Join-Path $Dir 'guild-tracker-export.xlsx') -Content 'placeholder real-looking guild tracker bytes'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'quest-picker.html') -Content '<html><body>Real picker, not sanitized.</body></html>'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'waypoints.json') -Content '{"note":"dummy real-named waypoints file"}'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-c-tailnet.txt') -Content 'Reach it at derek-omen.tail4f9c.ts.net for debugging.'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-c-hostname.txt') -Content 'Debug host: i5-laptop was used for capture.'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-d-steamid.txt') -Content 'Reporter id=76561198012345678 filed this.'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-e-header.cfg') -Content 'x-hearth-key: totally-not-the-default-abc123'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-e-generic.cfg') -Content 'secret = "stripe-token-redacted"'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-f-users.txt') -Content 'Local path: C:\Users\derek\projects\secret-notes'

    Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-f-commandcenter.txt') -Content 'See C:\work\commandcenter\.mcp.json for the key'

    if ($GcpIp) {
        Write-TextFileUtf8 -Path (Join-Path $Dir 'poison-g-ip.txt') -Content "Server lives at $GcpIp for now."
    }
}

# ----------------------------------------------------------------------------
# Self-test
# ----------------------------------------------------------------------------

function Invoke-SelfTest {
    Write-Host '=== Test-WorkbenchZipPrivacy SelfTest ==='
    Write-Host "Repo root: $Script:RepoRoot"

    try {
        $ctx = Get-ScanRules -RepoRoot $Script:RepoRoot
        Write-Host ("Loaded {0} real player handle(s) from waypoints.json." -f $ctx.OwnerCount)
        if ($ctx.GcpIp) {
            Write-Host ("Rule (g) GCP external IP: ACTIVE ({0})" -f $ctx.GcpIp)
        } else {
            Write-Host 'Rule (g) GCP external IP: INACTIVE (no unambiguous IP found) - g coverage will be skipped'
        }

        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $fixtureRoot = Join-Path $Script:ScratchpadRoot "workbench-zip-privacy-selftest\$stamp"
        $cleanDir = Join-Path $fixtureRoot 'clean'
        $poisonDir = Join-Path $fixtureRoot 'poisoned'

        $sampleOwner = $null
        if ($ctx.OwnerRawNames.Count -gt 0) {
            $sampleOwner = ($ctx.OwnerRawNames | Sort-Object -Property Length -Descending | Select-Object -First 1)
        } else {
            Write-Host 'No owner names loaded; rule (a) coverage will be skipped.'
        }

        New-CleanFixture -Dir $cleanDir
        New-PoisonedFixture -Dir $poisonDir -SampleOwner $sampleOwner -GcpIp $ctx.GcpIp

        $cleanFindings = @(Invoke-PrivacyScan -ScanRoot $cleanDir -Rules $ctx.Rules -TextExtensions $Script:TextExtensions)
        $poisonFindings = @(Invoke-PrivacyScan -ScanRoot $poisonDir -Rules $ctx.Rules -TextExtensions $Script:TextExtensions)

        $expectedRules = @(
            'b.xlsx-guild', 'b.quest-picker', 'b.waypoints-real',
            'c.tailnet-fqdn', 'c.hostname-i5laptop',
            'd.steamid64',
            'e.cred-header', 'e.cred-generic',
            'f.path-users-derek', 'f.path-commandcenter'
        )
        if ($sampleOwner) { $expectedRules += 'a.player-handle' }
        if ($ctx.GcpIp) { $expectedRules += 'g.gcp-external-ip' }

        $overallPass = $true

        Write-Host ''
        Write-Host '--- CLEAN fixture result ---'
        if ($cleanFindings.Count -eq 0) {
            Write-Host 'PASS: clean fixture produced 0 findings.'
        } else {
            $overallPass = $false
            Write-Host ("FAIL: clean fixture produced {0} finding(s):" -f $cleanFindings.Count)
            foreach ($f in $cleanFindings) {
                Write-Host ("   [{0}] {1} :: {2} :: {3}" -f $f.Rule, $f.File, $f.ScanType, $f.Match)
            }
        }

        Write-Host ''
        Write-Host '--- POISONED fixture per-rule coverage ---'
        $poisonRuleIds = @($poisonFindings | Select-Object -ExpandProperty Rule -Unique)
        foreach ($ruleId in $expectedRules) {
            if ($poisonRuleIds -contains $ruleId) {
                Write-Host "PASS: $ruleId caught"
            } else {
                $overallPass = $false
                Write-Host "FAIL: $ruleId NOT caught"
            }
        }

        # Bonus: exercise the actual -Path zip flow (Expand-Archive + temp-dir
        # cleanup), not just the direct-directory scan path used above.
        Write-Host ''
        Write-Host '--- BONUS: zip-path (Expand-Archive) round trip on the poisoned fixture ---'
        $zipPath = Join-Path $fixtureRoot 'poisoned.zip'
        try {
            Compress-Archive -Path (Join-Path $poisonDir '*') -DestinationPath $zipPath -Force
            $zipResult = Invoke-PathScan -InputPath $zipPath -Rules $ctx.Rules -TextExtensions $Script:TextExtensions
            if ($zipResult.ErrorMessage) {
                $overallPass = $false
                Write-Host "FAIL: zip-path scan errored: $($zipResult.ErrorMessage)"
            } else {
                $zipRuleIds = @($zipResult.Findings | Select-Object -ExpandProperty Rule -Unique)
                $zipOk = $true
                foreach ($ruleId in $expectedRules) {
                    if (-not ($zipRuleIds -contains $ruleId)) { $zipOk = $false }
                }
                if ($zipOk) {
                    Write-Host ("PASS: zip-path extraction reproduces all {0} rule finding(s)." -f $expectedRules.Count)
                } else {
                    $overallPass = $false
                    Write-Host ("FAIL: zip-path extraction did not reproduce all expected findings. Got: {0}" -f ($zipRuleIds -join ', '))
                }
            }
        } catch {
            $overallPass = $false
            Write-Host "FAIL: zip-path round trip threw: $($_.Exception.Message)"
        }

        Write-Host ''
        if ($overallPass) {
            Write-Host ("SELFTEST RESULT: PASS ({0} rule(s) verified, clean fixture 0 findings)" -f $expectedRules.Count)
        } else {
            Write-Host 'SELFTEST RESULT: FAIL'
        }
        Write-Host "Fixtures retained at: $fixtureRoot"

        if ($overallPass) { return 0 } else { return 1 }
    } catch {
        Write-Host "SELFTEST RESULT: FAIL (unhandled exception: $($_.Exception.Message))"
        Write-Host $_.ScriptStackTrace
        return 1
    }
}

# ----------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------

if ($SelfTest) {
    $code = Invoke-SelfTest
    exit $code
}

if (-not $Path) {
    Write-Host 'Usage:'
    Write-Host '  Test-WorkbenchZipPrivacy.ps1 -Path <zip-file-or-directory>'
    Write-Host '  Test-WorkbenchZipPrivacy.ps1 -SelfTest'
    exit 2
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "Path not found: $Path"
    exit 2
}

$pathItem = Get-Item -LiteralPath $Path
if (-not $pathItem.PSIsContainer -and $pathItem.Extension -ine '.zip') {
    Write-Host "Path must be a .zip file or a directory: $Path"
    exit 2
}

$ctx = Get-ScanRules -RepoRoot $Script:RepoRoot
$scanResult = Invoke-PathScan -InputPath $Path -Rules $ctx.Rules -TextExtensions $Script:TextExtensions

if ($scanResult.ErrorMessage) {
    Write-Host "ERROR: $($scanResult.ErrorMessage)"
    exit 2
}

Write-ScanReport -Findings $scanResult.Findings -SourcePath $Path -Context $ctx

$findingsCount = @($scanResult.Findings).Count
if ($findingsCount -gt 0) {
    exit 1
} else {
    exit 0
}
