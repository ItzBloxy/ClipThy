<#
.SYNOPSIS
Measures A/V offset and dropped frames in a clip recorded while "ClipThy.exe --sync-test" was running.

.DESCRIPTION
The sync test window flashes white and beeps at the same moment once per second. This script finds the
flashes (jumps in average brightness) and the beeps (ends of silence on the system audio track), pairs
them, and reports audio-minus-video offset. It also counts dropped frames from gaps in frame timestamps.
Positive offset = audio late. The test window itself adds roughly 10-20 ms (its beep is generated a
buffer ahead of the flash being drawn), so read results with that in mind.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\sync-check.ps1 -Clip "C:\Users\BLOXY\Documents\ShareX\Screenshots\2026-10\clip_2026-10-01_12-00-00.mp4"
#>
param(
    [Parameter(Mandatory = $true)][string]$Clip,
    [double]$FlashThreshold = 15,
    [string]$Ffmpeg = "ffmpeg",
    [string]$Ffprobe = "ffprobe"
)

$ErrorActionPreference = "Stop"
$clipPath = (Resolve-Path $Clip).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("clipthy-sync-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $work | Out-Null
Push-Location $work
try {
    # Frame times and average brightness. Relative file names avoid escaping "C:" inside filter arguments.
    & $Ffmpeg -hide_banner -nostdin -v error -i $clipPath -map 0:v:0 -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=yavg.txt" -f null NUL
    $frames = New-Object System.Collections.Generic.List[object]
    $time = $null
    foreach ($line in Get-Content yavg.txt) {
        if ($line -match "pts_time:([0-9.]+)") { $time = [double]$Matches[1] }
        elseif ($line -match "YAVG=([0-9.]+)") { $frames.Add([pscustomobject]@{ Time = $time; Y = [double]$Matches[1] }) }
    }
    $flashes = @()
    for ($i = 1; $i -lt $frames.Count; $i++) {
        if ($frames[$i].Y - $frames[$i - 1].Y -gt $FlashThreshold) { $flashes += $frames[$i].Time }
    }

    # Beep onsets: the System track of a multitrack (Alt+F11) clip, otherwise the only track.
    $audioTracks = @(& $Ffprobe -v error -select_streams a -show_entries stream=index -of csv=p=0 $clipPath).Count
    $track = 0
    if ($audioTracks -ge 3) { $track = 1 }
    & $Ffmpeg -hide_banner -nostdin -v error -i $clipPath -map "0:a:$track" -af "silencedetect=noise=-30dB:d=0.3,ametadata=print:key=lavfi.silence_end:file=silence.txt" -f null NUL
    $beeps = @()
    foreach ($line in Get-Content silence.txt) {
        if ($line -match "lavfi.silence_end=([0-9.]+)") { $beeps += [double]$Matches[1] }
    }

    # Pair each flash with the nearest beep within half a second.
    $offsets = @()
    foreach ($flash in $flashes) {
        $nearest = $null
        foreach ($beep in $beeps) {
            if ($null -eq $nearest -or [Math]::Abs($beep - $flash) -lt [Math]::Abs($nearest - $flash)) { $nearest = $beep }
        }
        if ($null -ne $nearest -and [Math]::Abs($nearest - $flash) -le 0.5) { $offsets += ($nearest - $flash) * 1000 }
    }

    # Dropped frames: gaps longer than 1.5x the typical frame interval.
    $intervals = @()
    for ($i = 1; $i -lt $frames.Count; $i++) { $intervals += $frames[$i].Time - $frames[$i - 1].Time }
    $sorted = $intervals | Sort-Object
    $median = $sorted[[int]($sorted.Count / 2)]
    $dropped = 0
    foreach ($interval in $intervals) {
        if ($interval -gt 1.5 * $median) { $dropped += [Math]::Round($interval / $median) - 1 }
    }
    $droppedPercent = 100.0 * $dropped / ($frames.Count + $dropped)

    Write-Host ("Dropped frames: {0} of {1} ({2:F2} %)" -f $dropped, ($frames.Count + $dropped), $droppedPercent)
    Write-Host ("Flashes: {0}, beeps: {1}, matched: {2}" -f $flashes.Count, $beeps.Count, $offsets.Count)
    if ($offsets.Count -eq 0) {
        Write-Host "No flash/beep pairs found, so no A/V offset (expected for a normal clip; for a sync test, was the window on the primary monitor with sound on?)"
        exit 2
    }
    $stats = $offsets | Measure-Object -Average -Minimum -Maximum
    Write-Host ("A/V offset (audio minus video): mean {0:F1} ms, min {1:F1} ms, max {2:F1} ms (positive = audio late)" -f $stats.Average, $stats.Minimum, $stats.Maximum)

    $pass = [Math]::Abs($stats.Average) -le 40 -and $droppedPercent -le 1
    if ($pass) { Write-Host "PASS (|mean offset| <= 40 ms, dropped frames <= 1 %)" } else { Write-Host "FAIL (targets: |mean offset| <= 40 ms, dropped frames <= 1 %)" }
    if ($pass) { exit 0 } else { exit 1 }
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work
}
