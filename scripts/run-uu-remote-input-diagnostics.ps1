<#
.SYNOPSIS
  UU remote input dual-channel diagnostics (Raw Input + WH_MOUSE_LL).

.PARAMETER ControllerPlatform
  WINDOWS_CONTROLLER | MACOS_CONTROLLER | UNKNOWN
  Do not guess; leave UNKNOWN if unsure.
#>
param(
  [ValidateSet("UNKNOWN", "WINDOWS_CONTROLLER", "MACOS_CONTROLLER")]
  [string]$ControllerPlatform = "UNKNOWN",
  [string]$OutRoot = "",
  [switch]$SkipInteractiveUuTests,
  [switch]$SkipConsolePhysical,
  [switch]$DeviceSnapshotOnly
)

$ErrorActionPreference = "Stop"
$dotnet = if (Test-Path "C:\Users\HanXuyi\Desktop\Agnet COmpany\.dotnet10\dotnet.exe") {
  "C:\Users\HanXuyi\Desktop\Agnet COmpany\.dotnet10\dotnet.exe"
} else { "dotnet" }

$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (-not $OutRoot) {
  $OutRoot = Join-Path $repo "docs\physical-input-sse\uu-remote-diagnostics"
}
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$exeProject = "tests\InputExecutor.RemoteInputDiagnostics\InputExecutor.RemoteInputDiagnostics.csproj"

function Invoke-Diag {
  param(
    [string]$Label,
    [int]$Seconds,
    [string]$Stimulus = "none",
    [string]$Prompt = "",
    [string]$Mode = "capture"
  )
  $extra = @()
  if ($Prompt) { $extra += @("--prompt", $Prompt) }
  if ($Mode -ne "capture") { $extra += @("--mode", $Mode) }
  & $dotnet run --project $exeProject -c Release --no-build -- `
    --out $OutRoot --label $Label --seconds $Seconds `
    --controller $ControllerPlatform --stimulus $Stimulus @extra
  if ($LASTEXITCODE -ne 0) { throw "diagnostics failed for $Label" }
}

function Read-LatestVerdict([string]$LabelPattern) {
  $dir = Get-ChildItem $OutRoot -Directory | Where-Object { $_.Name -like "*$LabelPattern*" } | Sort-Object Name | Select-Object -Last 1
  if (-not $dir) { return $null }
  $vp = Join-Path $dir.FullName "stimulus-verdict.json"
  if (Test-Path $vp) { return Get-Content $vp -Raw | ConvertFrom-Json }
  return $null
}

Write-Host "Building Release..."
& $dotnet build InputExecutor.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "=== Coordinate snapshot (DPI / display spaces) ==="
Invoke-Diag -Label "coordinate-env" -Seconds 0 -Mode "coordinate-snapshot"

Write-Host "=== Device snapshot BEFORE UU (disconnect UU if connected) ==="
Write-Host "Press Enter after UU is disconnected (or continue if already disconnected)."
[void][Console]::ReadLine()
Invoke-Diag -Label "uu-devices-before" -Seconds 0 -Mode "device-snapshot"
$beforeDir = Get-ChildItem $OutRoot -Directory | Where-Object { $_.Name -like "*uu-devices-before-device-snapshot*" } | Sort-Object Name | Select-Object -Last 1
$beforeEnv = Join-Path $beforeDir.FullName "session-environment.json"

if ($DeviceSnapshotOnly) {
  Write-Host "DeviceSnapshotOnly: connect UU, press Enter for AFTER snapshot."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "uu-devices-after" -Seconds 0 -Mode "device-snapshot"
  $afterDir = Get-ChildItem $OutRoot -Directory | Where-Object { $_.Name -like "*uu-devices-after-device-snapshot*" } | Sort-Object Name | Select-Object -Last 1
  $afterEnv = Join-Path $afterDir.FullName "session-environment.json"
  & $dotnet run --project $exeProject -c Release --no-build -- `
    --out $OutRoot --label "uu-devices" --mode device-compare --before $beforeEnv --after $afterEnv
  Write-Host "Device snapshot-only done."
  exit 0
}

Write-Host "=== TEST E: untagged mouse_event (automated) ==="
Invoke-Diag -Label "test-e-untagged-mouse-event" -Seconds 6 -Stimulus "untagged-mouse-event"

Write-Host "=== TEST F: tagged SendInput (automated) ==="
Invoke-Diag -Label "test-f-tagged-sendinput" -Seconds 6 -Stimulus "tagged-sendinput"

if (-not $SkipInteractiveUuTests) {
  Write-Host ""
  Write-Host "UU interactive tests require Founder to operate the UU controller."
  Write-Host "ControllerPlatform=$ControllerPlatform"
  Write-Host "Connect UU now if needed, then press Enter for AFTER device snapshot."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "uu-devices-after" -Seconds 0 -Mode "device-snapshot"
  $afterDir = Get-ChildItem $OutRoot -Directory | Where-Object { $_.Name -like "*uu-devices-after-device-snapshot*" } | Sort-Object Name | Select-Object -Last 1
  $afterEnv = Join-Path $afterDir.FullName "session-environment.json"
  & $dotnet run --project $exeProject -c Release --no-build -- `
    --out $OutRoot --label "uu-devices" --mode device-compare --before $beforeEnv --after $afterEnv

  Write-Host ""
  Write-Host "TEST A: MOVE ONLY — move mouse via UU for ~6s. NO wheel. NO click."
  Write-Host "Press Enter when ready, then MOVE ONLY."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-a-uu-move" -Seconds 6 -Prompt "MOVE ONLY via UU. Do NOT scroll. Do NOT click."
  $va = Read-LatestVerdict "test-a-uu-move"
  Write-Host ("TEST A verdict: {0} — {1}" -f $va.status, $va.reason)

  Write-Host ""
  Write-Host "TEST B-LEFT: LEFT click 2-3 times via UU. No right click."
  Write-Host "Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-b-uu-left-clicks" -Seconds 8 -Prompt "LEFT click only via UU (down+up). No right click."
  $vbl = Read-LatestVerdict "test-b-uu-left-clicks"
  Write-Host ("TEST B-LEFT verdict: {0} — {1}" -f $vbl.status, $vbl.reason)

  Write-Host ""
  Write-Host "TEST B-RIGHT: RIGHT click 2-3 times via UU. No left click."
  Write-Host "Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-b-uu-right-clicks" -Seconds 8 -Prompt "RIGHT click only via UU (down+up). No left click."
  $vbr = Read-LatestVerdict "test-b-uu-right-clicks"
  Write-Host ("TEST B-RIGHT verdict: {0} — {1}" -f $vbr.status, $vbr.reason)

  Write-Host ""
  Write-Host "TEST C-VWHEEL: vertical scroll only via UU."
  Write-Host "Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-c-uu-wheel" -Seconds 8 -Prompt "Vertical mouse wheel ONLY via UU."
  $vcv = Read-LatestVerdict "test-c-uu-wheel"
  Write-Host ("TEST C-VWHEEL verdict: {0} — {1}" -f $vcv.status, $vcv.reason)

  Write-Host ""
  Write-Host "TEST C-HWHEEL: horizontal scroll only via UU."
  Write-Host "Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-c-uu-hwheel" -Seconds 8 -Prompt "Horizontal mouse wheel ONLY via UU."
  $vch = Read-LatestVerdict "test-c-uu-hwheel"
  Write-Host ("TEST C-HWHEEL verdict: {0} — {1}" -f $vch.status, $vch.reason)

  Write-Host ""
  Write-Host "TEST D: DRAG — hold LEFT button, move >=200px, then release."
  Write-Host "Must have WM_MOUSEMOVE between down and up. Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-d-uu-drag-select" -Seconds 12 -Prompt "Hold LEFT, drag >=200px, release. Keep moving while held."
  $vd = Read-LatestVerdict "test-d-uu-drag-select"
  Write-Host ("TEST D verdict: {0} — {1}" -f $vd.status, $vd.reason)
} else {
  Write-Host "Skipped interactive UU tests (SkipInteractiveUuTests)."
}

if (-not $SkipConsolePhysical) {
  Write-Host ""
  Write-Host "TEST G: Console PHYSICAL mouse on the controlled PC (NOT UU)."
  Write-Host "If you cannot operate the local console mouse, type NOT_RUN and Enter."
  Write-Host "Otherwise press Enter and use the LOCAL physical mouse for 8s."
  $ans = Read-Host
  if ($ans -eq "NOT_RUN") {
    $dir = Join-Path $OutRoot ("{0}-test-g-console-physical-NOT_RUN" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    '{"status":"NOT_RUN","reason":"cannot operate console physical mouse on controlled PC"}' |
      Set-Content -Encoding utf8 (Join-Path $dir "result.json")
    '{"label":"test-g-console-physical","status":"NOT_RUN","reason":"operator cannot use local console mouse"}' |
      Set-Content -Encoding utf8 (Join-Path $dir "stimulus-verdict.json")
    Write-Host "TEST G marked NOT_RUN -> $dir"
  } else {
    Invoke-Diag -Label "test-g-console-physical" -Seconds 8 -Prompt "Use LOCAL physical mouse only (not UU)."
    $vg = Read-LatestVerdict "test-g-console-physical"
    Write-Host ("TEST G verdict: {0} — {1}" -f $vg.status, $vg.reason)
  }
} else {
  Write-Host "Skipped TEST G."
}

Write-Host "Generating summary report..."
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "summarize-uu-remote-input-diagnostics.ps1") -OutRoot $OutRoot -ControllerPlatform $ControllerPlatform
Write-Host "Done. Evidence under $OutRoot"
Write-Host "Remember: never mark PASS for actions that did not occur."
