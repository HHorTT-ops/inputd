param(
    [string]$ResultPath = (Join-Path ([IO.Path]::GetTempPath()) 'inputd-obs-live-result.json'),
    [string]$ObsExecutable = '',
    [string]$RecordingDirectory = (Join-Path $env:USERPROFILE 'Videos'),
    [int[]]$CleanupProcessIds = @()
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ObsExecutable)) {
    $candidates = @(
        $env:INPUTD_OBS_EXECUTABLE,
        (Join-Path $env:ProgramFiles 'obs-studio\bin\64bit\obs64.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\obs-studio\bin\64bit\obs64.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $ObsExecutable = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($ObsExecutable)) {
        throw 'OBS was not found. Pass -ObsExecutable or set INPUTD_OBS_EXECUTABLE.'
    }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InputdObsAcceptanceNative {
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
'@

function Close-ObsSafely([Diagnostics.Process]$Process) {
    $Process.Refresh()
    if ($Process.HasExited) { return }
    if (-not $Process.CloseMainWindow()) { throw "Could not request a safe OBS shutdown for PID $($Process.Id)." }
    if (-not $Process.WaitForExit(30000)) { throw "OBS PID $($Process.Id) did not close within 30 seconds." }
}

function Set-ConfirmOnExit([string]$Path, [bool]$Enabled) {
    $content = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $value = if ($Enabled) { 'true' } else { 'false' }
    if ($content -notmatch '(?m)^ConfirmOnExit=(true|false)$') { throw 'ConfirmOnExit was not found in OBS global.ini.' }
    $content = [regex]::Replace($content, '(?m)^ConfirmOnExit=(true|false)$', "ConfirmOnExit=$value")
    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8 -NoNewline
}

$globalIni = Join-Path $env:APPDATA 'obs-studio\global.ini'
$globalText = Get-Content -LiteralPath $globalIni -Raw -Encoding UTF8
$originalConfirm = [regex]::Match($globalText, '(?m)^ConfirmOnExit=(true|false)$').Groups[1].Value -eq 'true'
$configurationChanged = $false
$recordingObs = $null
$caught = $null
$result = $null

try {
    foreach ($processId in $CleanupProcessIds) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
    foreach ($existing in @(Get-Process obs64 -ErrorAction SilentlyContinue)) { Close-ObsSafely $existing }
    $bridgeDeadline = [Environment]::TickCount64 + 10000
    while ([Environment]::TickCount64 -lt $bridgeDeadline -and (Get-Process inputd-obs-bridge -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 100
    }
    & (Join-Path $PSScriptRoot 'publish-obs-plugin.ps1')
    & (Join-Path $PSScriptRoot 'install-obs-plugin.ps1')
    Set-ConfirmOnExit $globalIni $false
    $configurationChanged = $true

    $existingFiles = @{}
    Get-ChildItem -LiteralPath $RecordingDirectory -File -ErrorAction SilentlyContinue | ForEach-Object { $existingFiles[$_.FullName] = $true }
    $spoolDirectory = Join-Path $env:LOCALAPPDATA 'inputd\obs-spool'
    $existingPartials = @{}
    Get-ChildItem -LiteralPath $spoolDirectory -File -Filter '*.partial' -ErrorAction SilentlyContinue |
        ForEach-Object { $existingPartials[$_.FullName] = $true }
    $startedAt = Get-Date
    $recordingObs = Start-Process -FilePath $ObsExecutable -WorkingDirectory (Split-Path -Parent $ObsExecutable) `
        -ArgumentList '--startrecording' -PassThru
    $recordingObsProcessId = $recordingObs.Id

    $deadline = [Environment]::TickCount64 + 45000
    $video = $null
    $partial = $null
    while ([Environment]::TickCount64 -lt $deadline) {
        $video = Get-ChildItem -LiteralPath $RecordingDirectory -File -ErrorAction SilentlyContinue |
            Where-Object { -not $existingFiles.ContainsKey($_.FullName) -and $_.LastWriteTime -ge $startedAt.AddSeconds(-2) -and $_.Name -notmatch '\.input\.jsonl' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $partial = Get-ChildItem -LiteralPath $spoolDirectory -File -Filter '*.partial' -ErrorAction SilentlyContinue |
            Where-Object { -not $existingPartials.ContainsKey($_.FullName) -and $_.LastWriteTime -ge $startedAt.AddSeconds(-2) } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $video -and $null -ne $partial) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $video) { throw 'OBS did not create a video file after --startrecording.' }
    if ($null -eq $partial) { throw 'The input bridge did not create a provisional lossless trajectory.' }

    [InputdObsAcceptanceNative]::mouse_event(0x0001, 13, 6, 0, [UIntPtr]::Zero)
    [InputdObsAcceptanceNative]::keybd_event(0x77, 0x42, 0x0008, [UIntPtr]::Zero)
    [InputdObsAcceptanceNative]::keybd_event(0x77, 0x42, 0x0008 -bor 0x0002, [UIntPtr]::Zero)
    [InputdObsAcceptanceNative]::mouse_event(0x0001, [uint32]::MaxValue - 12, [uint32]::MaxValue - 5, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700

    Close-ObsSafely $recordingObs
    $recordingObs = $null
    $trajectory = Join-Path $video.DirectoryName ($video.BaseName + '.input.jsonl')
    $manifestPath = $trajectory + '.manifest.json'
    $deadline = [Environment]::TickCount64 + 15000
    while ([Environment]::TickCount64 -lt $deadline -and -not (Test-Path -LiteralPath $manifestPath)) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path -LiteralPath $trajectory)) { throw "The finalized paired trajectory was not created beside $($video.FullName)." }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'The paired trajectory manifest was not created.' }

    $records = @(Get-Content -LiteralPath $trajectory | ForEach-Object { $_ | ConvertFrom-Json })
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $result = [ordered]@{
        ok = $manifest.complete -eq $true -and $manifest.events_dropped -eq 0 -and $manifest.events_pending -eq 0
        obs_process_id = $recordingObsProcessId
        video_path = $video.FullName
        trajectory_path = $trajectory
        manifest_path = $manifestPath
        records = $records.Count
        first_sequence = if ($records.Count -gt 0) { $records[0].sequence } else { $null }
        last_sequence = if ($records.Count -gt 0) { $records[-1].sequence } else { $null }
        sequence_gaps = if ($records.Count -gt 0) { (Compare-Object (1..$records.Count) @($records.sequence)).Count } else { 0 }
        positive_dx = @($records | Where-Object { $_.action_requested.mouse.dx -gt 0 }).Count
        negative_dx = @($records | Where-Object { $_.action_requested.mouse.dx -lt 0 }).Count
        key_down = @($records | Where-Object { $_.action_requested.changed_key -and $_.action_requested.key_down -eq $true }).Count
        key_up = @($records | Where-Object { $_.action_requested.changed_key -and $_.action_requested.key_down -eq $false }).Count
        events_dropped = $manifest.events_dropped
        events_pending = $manifest.events_pending
        trajectory_sha256 = $manifest.trajectory_sha256
    }
    if (-not $result.ok) { throw 'OBS paired recording integrity verification failed.' }
}
catch { $caught = $_ }
finally {
    if ($null -ne $recordingObs) {
        try { Close-ObsSafely $recordingObs } catch { if ($null -eq $caught) { $caught = $_ } }
    }
    if ($configurationChanged) {
        try { Set-ConfirmOnExit $globalIni $originalConfirm } catch { if ($null -eq $caught) { $caught = $_ } }
    }
    if (-not (Get-Process obs64 -ErrorAction SilentlyContinue)) {
        try { Start-Process -FilePath $ObsExecutable -WorkingDirectory (Split-Path -Parent $ObsExecutable) | Out-Null }
        catch { if ($null -eq $caught) { $caught = $_ } }
    }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
if ($null -ne $caught) {
    ($caught | Out-String) | Set-Content -LiteralPath ($ResultPath + '.error.txt') -Encoding UTF8
    throw $caught
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
