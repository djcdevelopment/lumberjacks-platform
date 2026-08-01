<#
.SYNOPSIS
Bounded screen capture of a Valheim client for C9 motion-quality evidence.

.DESCRIPTION
Records a fixed-length clip of one client's screen and writes a receipt carrying
the wall-clock start, so the frames can later be aligned against that client's
motion telemetry.

An ssh session on the i5 lands in a non-interactive session and gdigrab fails
there with "Failed to capture image (error 5)". This uses the same interactive
scheduled-task idiom the native client harness already relies on
(Invoke-NativeValheimClient.ps1 Install-InteractiveTask): queue a request file,
start the task, let it run inside the logged-on desktop session.

On OMEN, where the agent already runs interactively, 'capture-now' skips the
task entirely.

.EXAMPLE
  # once per machine
  .\Invoke-MotionClipCapture.ps1 -Action install-task

  # remote (i5), from the OMEN side over ssh
  .\Invoke-MotionClipCapture.ps1 -Action queue -DurationSeconds 40 -Label c9-i5-observe

  # local (OMEN)
  .\Invoke-MotionClipCapture.ps1 -Action capture-now -DurationSeconds 40 -Label c9-omen-observe
#>
[CmdletBinding()]
param(
    [ValidateSet('install-task', 'queue', 'capture-now', 'run-pending', 'status', 'stop')]
    [string] $Action = 'status',

    [string] $TaskName = 'BaselineMotionClipCapture',

    [string] $PendingRequestPath = 'C:\deploy\baseline\motion-clip-request.json',

    [ValidateRange(2, 600)]
    [int] $DurationSeconds = 30,

    [string] $OutputDirectory,

    [string] $Label = 'motion-clip',

    [ValidateRange(5, 60)]
    [int] $Fps = 30,

    # Output is downscaled: a 4K side-by-side is unreviewable and enormous.
    [ValidateRange(320, 3840)]
    [int] $Width = 1280,

    # 'desktop' or a gdigrab window selector such as 'title=Valheim'.
    [string] $Source = 'desktop',

    # A software encoder competes with the game for CPU and would perturb the
    # very frame timing C9 measures. Prefer the GPU encoder per machine
    # (h264_nvenc on OMEN's RTX, h264_qsv on the i5's Iris Xe); falls back to
    # libx264 if the hardware path refuses.
    [ValidateSet('h264_nvenc', 'h264_qsv', 'h264_amf', 'libx264')]
    [string] $Encoder = 'libx264',

    [string] $FfmpegPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Resolve-Ffmpeg([string] $Explicit) {
    if ($Explicit -and (Test-Path -LiteralPath $Explicit -PathType Leaf)) { return $Explicit }
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'),
        'C:\ProgramData\chocolatey\bin'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    foreach ($root in $roots) {
        $hit = Get-ChildItem -LiteralPath $root -Filter 'ffmpeg.exe' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw 'ffmpeg.exe not found; install Gyan.FFmpeg or pass -FfmpegPath.'
}

function Resolve-OutputDirectory([string] $Requested) {
    if ($Requested) { return $Requested }
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $candidate = Join-Path $repoRoot 'fieldlab\runs\motion-clips'
    # The i5 has no repo checkout; fall back to its deploy staging root.
    if (Test-Path -LiteralPath (Split-Path -Parent $candidate)) { return $candidate }
    return 'C:\deploy\baseline\motion-clips'
}

function Write-JsonAtomic([string] $Path, $Value) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    $temp = "$Path.tmp"
    ($Value | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Test-SafeToken([string] $Value) {
    return ($Value -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')
}

function Invoke-Capture($Request) {
    $ffmpeg = Resolve-Ffmpeg $Request.ffmpeg_path
    $outDir = $Request.output_directory
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $clipPath = Join-Path $outDir ("{0}-{1}.mp4" -f $Request.label, $stamp)
    $logPath = "$clipPath.ffmpeg.log"

    $encoder = if ($Request.encoder) { [string] $Request.encoder } else { 'libx264' }
    function Get-FfArgs([string] $Codec, [string] $Destination) {
        # Preset vocabularies differ per encoder family; nvenc's p1-p7 is a
        # parse error to qsv, and amf uses its own names again.
        $quality = switch ($Codec) {
            'h264_nvenc' { @('-preset', 'p4', '-cq', '25') }
            'h264_qsv'   { @('-preset', 'veryfast', '-global_quality', '25') }
            'h264_amf'   { @('-quality', 'balanced', '-rc', 'cqp', '-qp_p', '25', '-qp_i', '25') }
            default      { @('-preset', 'veryfast', '-crf', '23') }
        }
        @(
            '-hide_banner', '-loglevel', 'warning', '-y',
            '-f', 'gdigrab',
            '-framerate', [string] $Request.fps,
            '-t', [string] $Request.duration_seconds,
            '-i', $Request.source,
            '-vf', ("scale={0}:-2" -f $Request.width)
        ) + @('-c:v', $Codec) + $quality + @('-pix_fmt', 'yuv420p', $Destination)
    }
    $ffArgs = Get-FfArgs $encoder $clipPath

    # Stamped as close to the first grabbed frame as this lane allows. Process
    # start adds a small unmeasured offset, declared in the receipt rather than
    # silently ignored, because clip/telemetry alignment depends on it.
    $startedUtc = [DateTimeOffset]::UtcNow
    $proc = Start-Process -FilePath $ffmpeg -ArgumentList $ffArgs -NoNewWindow -PassThru `
        -RedirectStandardError $logPath
    # Touching Handle forces PowerShell to cache it; without this ExitCode reads
    # back null after WaitForExit and a good capture looks like a failure.
    $null = $proc.Handle
    $proc.WaitForExit()
    $finishedUtc = [DateTimeOffset]::UtcNow
    $exitCode = $proc.ExitCode

    $encoderFallback = $false
    if ($exitCode -ne 0 -and $encoder -ne 'libx264') {
        # The encoder list is compile-time, so a missing GPU only shows up here.
        $encoderFallback = $true
        $encoder = 'libx264'
        $startedUtc = [DateTimeOffset]::UtcNow
        # Separate log so the hardware encoder's actual refusal survives.
        $logPath = "$clipPath.ffmpeg-fallback.log"
        $proc = Start-Process -FilePath $ffmpeg -ArgumentList (Get-FfArgs $encoder $clipPath) `
            -NoNewWindow -PassThru -RedirectStandardError $logPath
        $null = $proc.Handle
        $proc.WaitForExit()
        $finishedUtc = [DateTimeOffset]::UtcNow
        $exitCode = $proc.ExitCode
    }

    $exists = Test-Path -LiteralPath $clipPath -PathType Leaf
    $clipBytes = if ($exists) { (Get-Item -LiteralPath $clipPath).Length } else { 0 }
    # The artifact is the receipt, not the exit code: a clip with real bytes and
    # a probeable duration is a capture even if ffmpeg grumbled on the way out.
    $probedDuration = $null
    if ($exists -and $clipBytes -gt 0) {
        $ffprobe = Join-Path (Split-Path -Parent $ffmpeg) 'ffprobe.exe'
        if (Test-Path -LiteralPath $ffprobe) {
            $probe = & $ffprobe -hide_banner -v error -show_entries format=duration `
                -of default=noprint_wrappers=1:nokey=1 $clipPath
            if ($probe) { $probedDuration = [double] ($probe | Select-Object -First 1) }
        }
    }
    $ok = $exists -and $clipBytes -gt 0 -and ($null -eq $probedDuration -or $probedDuration -gt 0)

    $receipt = [ordered] @{
        schema_version              = 1
        receipt_type                = 'motion_clip_capture'
        label                       = $Request.label
        machine                     = $env:COMPUTERNAME
        result                      = if ($ok) { 'captured' } else { 'failed' }
        exit_code                   = $exitCode
        probed_duration_seconds     = $probedDuration
        clip_path                   = $clipPath
        clip_bytes                  = $clipBytes
        source                      = $Request.source
        encoder                     = $encoder
        encoder_fallback_used       = $encoderFallback
        fps                         = $Request.fps
        width                       = $Request.width
        requested_duration_seconds  = $Request.duration_seconds
        capture_started_utc         = $startedUtc.ToString('o')
        capture_finished_utc        = $finishedUtc.ToString('o')
        observed_duration_seconds   = [math]::Round(($finishedUtc - $startedUtc).TotalSeconds, 3)
        start_offset_uncertainty_ms = 'process start to first frame is unmeasured; treat alignment as +/- 300ms'
        ffmpeg_path                 = $ffmpeg
        ffmpeg_log                  = $logPath
    }
    $receiptPath = "$clipPath.receipt.json"
    Write-JsonAtomic $receiptPath $receipt
    $receipt['receipt_path'] = $receiptPath
    return $receipt
}

switch ($Action) {

    'install-task' {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $powerShellExe = Join-Path $PSHOME 'powershell.exe'
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Action run-pending -PendingRequestPath "{1}"' -f
            $PSCommandPath, $PendingRequestPath
        $taskAction = New-ScheduledTaskAction -Execute $powerShellExe -Argument $arguments
        $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) `
            -MultipleInstances IgnoreNew
        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $taskAction `
            -Principal $principal `
            -Settings $settings `
            -Force | Out-Null
        $task = Get-ScheduledTask -TaskName $TaskName
        [ordered] @{
            schema_version  = 1
            receipt_type    = 'motion_clip_task_install'
            generated_utc   = [DateTimeOffset]::UtcNow.ToString('o')
            task_name       = $TaskName
            state           = [string] $task.State
            principal       = $identity
            logon_type      = 'Interactive'
            entrypoint      = $PSCommandPath
            pending_request = $PendingRequestPath
            ffmpeg_path     = (Resolve-Ffmpeg $FfmpegPath)
        } | ConvertTo-Json -Depth 6
    }

    'queue' {
        if (-not (Test-SafeToken $Label)) { throw "Label is not a safe token: $Label" }
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        if ($task.State -eq 'Running') { throw "Capture task is already running: $TaskName" }
        if (Test-Path -LiteralPath $PendingRequestPath -PathType Leaf) {
            Remove-Item -LiteralPath $PendingRequestPath -Force
        }
        $request = [ordered] @{
            schema_version   = 1
            requested_utc    = [DateTimeOffset]::UtcNow.ToString('o')
            label            = $Label
            duration_seconds = $DurationSeconds
            output_directory = (Resolve-OutputDirectory $OutputDirectory)
            fps              = $Fps
            width            = $Width
            source           = $Source
            encoder          = $Encoder
            ffmpeg_path      = $FfmpegPath
        }
        Write-JsonAtomic $PendingRequestPath $request
        Start-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 1
        $task = Get-ScheduledTask -TaskName $TaskName
        [ordered] @{
            schema_version = 1
            receipt_type   = 'motion_clip_queued'
            generated_utc  = [DateTimeOffset]::UtcNow.ToString('o')
            task_name      = $TaskName
            task_state     = [string] $task.State
            request        = $request
        } | ConvertTo-Json -Depth 6
    }

    'run-pending' {
        if (-not (Test-Path -LiteralPath $PendingRequestPath -PathType Leaf)) {
            throw "No pending capture request at $PendingRequestPath"
        }
        $request = Get-Content -LiteralPath $PendingRequestPath -Raw | ConvertFrom-Json
        Remove-Item -LiteralPath $PendingRequestPath -Force
        (Invoke-Capture $request) | ConvertTo-Json -Depth 6
    }

    'capture-now' {
        if (-not (Test-SafeToken $Label)) { throw "Label is not a safe token: $Label" }
        $request = [pscustomobject] @{
            label            = $Label
            duration_seconds = $DurationSeconds
            output_directory = (Resolve-OutputDirectory $OutputDirectory)
            fps              = $Fps
            width            = $Width
            source           = $Source
            encoder          = $Encoder
            ffmpeg_path      = $FfmpegPath
        }
        (Invoke-Capture $request) | ConvertTo-Json -Depth 6
    }

    'status' {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        $outDir = Resolve-OutputDirectory $OutputDirectory
        $latest = $null
        if (Test-Path -LiteralPath $outDir) {
            $latest = Get-ChildItem -LiteralPath $outDir -Filter '*.receipt.json' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        }
        [ordered] @{
            schema_version   = 1
            receipt_type     = 'motion_clip_status'
            generated_utc    = [DateTimeOffset]::UtcNow.ToString('o')
            machine          = $env:COMPUTERNAME
            task_name        = $TaskName
            task_registered  = [bool] $task
            task_state       = if ($task) { [string] $task.State } else { 'absent' }
            output_directory = $outDir
            ffmpeg_present   = [bool] (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)
            latest_receipt   = if ($latest) {
                (Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json)
            } else { $null }
        } | ConvertTo-Json -Depth 8
    }

    'stop' {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($task -and $task.State -eq 'Running') { Stop-ScheduledTask -TaskName $TaskName }
        Get-Process ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        [ordered] @{
            schema_version = 1
            receipt_type   = 'motion_clip_stop'
            generated_utc  = [DateTimeOffset]::UtcNow.ToString('o')
            task_name      = $TaskName
            task_state     = if ($task) { [string] (Get-ScheduledTask -TaskName $TaskName).State } else { 'absent' }
        } | ConvertTo-Json -Depth 6
    }
}
