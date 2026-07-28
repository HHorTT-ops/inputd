param(
    [string]$ObsExecutable = '',
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
foreach ($processId in $CleanupProcessIds) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
$obsProcesses = @(Get-Process obs64 -ErrorAction SilentlyContinue)
foreach ($process in $obsProcesses) {
    if (-not $process.CloseMainWindow()) { throw "Could not request a safe OBS shutdown for PID $($process.Id)." }
}
foreach ($process in $obsProcesses) {
    if (-not $process.WaitForExit(15000)) { throw "OBS PID $($process.Id) did not close within 15 seconds; registration was not attempted." }
}
$bridgeDeadline = [Environment]::TickCount64 + 10000
while ([Environment]::TickCount64 -lt $bridgeDeadline -and (Get-Process inputd-obs-bridge -ErrorAction SilentlyContinue)) {
    Start-Sleep -Milliseconds 100
}

& (Join-Path $PSScriptRoot 'publish-obs-plugin.ps1')
if ($LASTEXITCODE -ne 0) { throw "OBS plugin publish failed with exit code $LASTEXITCODE." }
& (Join-Path $PSScriptRoot 'install-obs-plugin.ps1') -Register
if ($LASTEXITCODE -ne 0) { throw "OBS plugin registration failed with exit code $LASTEXITCODE." }

$globalIni = Join-Path $env:APPDATA 'obs-studio\global.ini'
$globalContent = Get-Content -LiteralPath $globalIni -Raw -Encoding UTF8
if ($globalContent -match '(?m)^ConfirmOnExit=(true|false)$') {
    $globalContent = [regex]::Replace($globalContent, '(?m)^ConfirmOnExit=(true|false)$', 'ConfirmOnExit=true')
    Set-Content -LiteralPath $globalIni -Value $globalContent -Encoding UTF8 -NoNewline
}

if (-not (Test-Path -LiteralPath $ObsExecutable)) { throw "OBS executable not found: $ObsExecutable" }
Start-Process -FilePath $ObsExecutable -WorkingDirectory (Split-Path -Parent $ObsExecutable)
