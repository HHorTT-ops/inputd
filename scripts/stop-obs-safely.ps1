param(
    [string]$ResultPath = (Join-Path ([IO.Path]::GetTempPath()) 'inputd-obs-stop-result.txt')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
try {
    $obs = Get-Process obs64 -ErrorAction Stop | Select-Object -First 1
    $requested = $obs.CloseMainWindow()
    if (-not $requested) { throw "CloseMainWindow returned false for OBS PID $($obs.Id)." }
    $exited = $obs.WaitForExit(30000)
    "requested=$requested exited=$exited pid=$($obs.Id)" | Set-Content -LiteralPath $ResultPath -Encoding ASCII
    if (-not $exited) { exit 2 }
}
catch {
    ($_ | Out-String) | Set-Content -LiteralPath ($ResultPath + '.error.txt') -Encoding UTF8
    exit 1
}
