Set-StrictMode -Version 2.0

function Get-ComfySha256 {
    param([Parameter(Mandatory=$true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-ComfyUtf8NoBom {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Text)
    $enc = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Text, $enc)
}

function Invoke-ComfyAtomicReplace {
    param(
        [Parameter(Mandatory=$true)][string]$Destination,
        [Parameter(Mandatory=$true)][scriptblock]$Writer
    )
    $dir = Split-Path -Parent $Destination
    if (!(Test-Path -LiteralPath $dir -PathType Container)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = Join-Path $dir ('.comfy-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        & $Writer $tmp
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            $backup = $Destination + '.comfy-replace-backup'
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
            [IO.File]::Replace($tmp, $Destination, $backup, $true)
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
        } else {
            [IO.File]::Move($tmp, $Destination)
        }
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

function Get-ComfySectionText {
    param([string]$Text, [string]$Section)
    $pattern = '(?ms)(^\[' + [regex]::Escape($Section) + '\]\s*\r?\n)(.*?)(?=^\[[^\r\n]+\]\s*$|\z)'
    $m = [regex]::Match($Text, $pattern)
    if (!$m.Success) { return $null }
    return $m.Value
}

function Merge-ComfyBepInExSection {
    param(
        [Parameter(Mandatory=$true)][string]$Text,
        [Parameter(Mandatory=$true)][hashtable]$Values,
        [string]$Section = 'Lumberjacks'
    )
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $header = '[' + $Section + ']'
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in ($Text -split "`r?`n", -1)) { [void]$lines.Add($line) }
    $start = -1; $end = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq $header) { $start = $i; break }
    }
    if ($start -ge 0) {
        for ($i = $start + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*\[[^\]]+\]\s*$') { $end = $i; break }
        }
        $existing = @{}
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($lines[$i] -match '^\s*([^#;=\s]+)\s*=') { $existing[$Matches[1]] = $i }
        }
        foreach ($key in $Values.Keys) {
            $line = [string]$key + '=' + [string]$Values[$key]
            if ($existing.ContainsKey($key)) { $lines[$existing[$key]] = $line } else { $lines.Insert($end, $line); $end++ }
        }
    } else {
        while ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') { $lines.RemoveAt($lines.Count - 1) }
        [void]$lines.Add(''); [void]$lines.Add($header)
        foreach ($key in $Values.Keys) { [void]$lines.Add([string]$key + '=' + [string]$Values[$key]) }
        [void]$lines.Add('')
    }
    return [string]::Join($newline, $lines.ToArray())
}

function Remove-ComfyBepInExKeys {
    param(
        [Parameter(Mandatory=$true)][string]$Text,
        [Parameter(Mandatory=$true)][string[]]$Keys,
        [string]$Section = 'Lumberjacks'
    )
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in ($Text -split "`r?`n", -1)) { [void]$lines.Add($line) }
    $start = -1; $end = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq ('[' + $Section + ']')) { $start = $i; break } }
    if ($start -lt 0) { return $Text }
    for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\[[^\]]+\]\s*$') { $end = $i; break } }
    $keySet = @{}
    foreach ($key in $Keys) { $keySet[[string]$key] = $true }
    for ($i = $end - 1; $i -gt $start; $i--) {
        if ($lines[$i] -match '^\s*([^#;=\s]+)\s*=') {
            if ($keySet.ContainsKey($Matches[1])) { $lines.RemoveAt($i) }
        }
    }
    return [string]::Join($newline, $lines.ToArray())
}

function Find-ComfySteamValheimInstall {
    param([string[]]$Roots)
    $candidates = New-Object System.Collections.Generic.List[string]
    $defaultRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Steam'),
        (Join-Path ${env:ProgramFiles} 'Steam'),
        (Join-Path ${env:LOCALAPPDATA} 'Steam')
    )
    foreach ($root in @($Roots) + $defaultRoots) {
        if (!$root -or !(Test-Path -LiteralPath $root -PathType Container)) { continue }
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (!(Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
        $raw = Get-Content -LiteralPath $vdf -Raw
        foreach ($m in [regex]::Matches($raw, '"path"\s+"([^"]+)"')) {
            $library = $m.Groups[1].Value.Replace('\\','\')
            $candidate = Join-Path $library 'steamapps\common\Valheim'
            $acf = Join-Path $library 'steamapps\appmanifest_892970.acf'
            if ((Test-Path -LiteralPath $acf -PathType Leaf) -and (Test-Path -LiteralPath $candidate -PathType Container)) { [void]$candidates.Add($candidate) }
        }
    }
    $unique = @($candidates | Sort-Object -Unique)
    if ($unique.Count -eq 1) { return $unique[0] }
    if ($unique.Count -eq 0) { throw 'Valheim Steam library was not discovered' }
    throw ('Multiple Valheim Steam libraries discovered: ' + ($unique -join ', '))
}

function Remove-ComfySensitiveText {
    param([Parameter(Mandatory=$true)][string]$Text, [string[]]$SensitiveValues = @())
    $out = $Text
    foreach ($value in $SensitiveValues) {
        if ($value) { $out = $out.Replace($value, '[REDACTED]') }
    }
    $patterns = @(
        '\b7656119\d{10}\b',
        '(?i)(?:client[_ -]?access[_ -]?key|telemetry[_ -]?key|admin[_ -]?key|bearer|invite[_ -]?token|password)\s*[=:]\s*[A-Za-z0-9+/_=-]{12,}',
        '(?im)^\s*(?:lumberjacksClientAccessKey|lumberjacksTelemetryKey)\s*=.*$'
    )
    foreach ($pattern in $patterns) { $out = [regex]::Replace($out, $pattern, { param($m) if ($m.Value -match '=') { $m.Value.Substring(0, $m.Value.IndexOf('=') + 1) + '[REDACTED]' } else { '[REDACTED]' } }) }
    return $out
}

function Get-ComfyBootstrap {
    param([Parameter(Mandatory=$true)][string]$Url)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -Method Get -TimeoutSec 15
        $status = [int]$response.StatusCode
        if ($response.Content -is [byte[]]) { $body = [Text.Encoding]::UTF8.GetString($response.Content) } else { $body = [string]$response.Content }
    } catch {
        $status = 0
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        $body = ''
    }
    $values = @{}
    foreach ($line in ($body -split "`r?`n")) {
        if ($line -match '^([^=\r\n]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
    }
    return [pscustomobject]@{ StatusCode = $status; Body = $body; Values = $values }
}

Export-ModuleMember -Function Get-ComfySha256,Write-ComfyUtf8NoBom,Invoke-ComfyAtomicReplace,Merge-ComfyBepInExSection,Remove-ComfyBepInExKeys,Find-ComfySteamValheimInstall,Remove-ComfySensitiveText,Get-ComfyBootstrap
