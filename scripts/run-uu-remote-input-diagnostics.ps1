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
  [switch]$SkipConsolePhysical
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
  param([string]$Label, [int]$Seconds, [string]$Stimulus = "none")
  & $dotnet run --project $exeProject -c Release --no-build -- `
    --out $OutRoot --label $Label --seconds $Seconds `
    --controller $ControllerPlatform --stimulus $Stimulus
  if ($LASTEXITCODE -ne 0) { throw "diagnostics failed for $Label" }
}

Write-Host "Building Release..."
& $dotnet build InputExecutor.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "=== TEST E: untagged mouse_event (automated) ==="
Invoke-Diag -Label "test-e-untagged-mouse-event" -Seconds 6 -Stimulus "untagged-mouse-event"

Write-Host "=== TEST F: tagged SendInput (automated) ==="
Invoke-Diag -Label "test-f-tagged-sendinput" -Seconds 6 -Stimulus "tagged-sendinput"

if (-not $SkipInteractiveUuTests) {
  Write-Host ""
  Write-Host "UU interactive tests require Founder to operate the UU controller."
  Write-Host "ControllerPlatform=$ControllerPlatform"
  Write-Host ""

  Write-Host "TEST A: MOVE only for 5s. Press Enter when ready, then MOVE via UU."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-a-uu-move" -Seconds 5

  Write-Host "TEST B: LEFT then RIGHT click. Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-b-uu-clicks" -Seconds 8

  Write-Host "TEST C: vertical then horizontal wheel. Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-c-uu-wheel" -Seconds 8

  Write-Host "TEST D: drag a window / select text. Press Enter when ready."
  [void][Console]::ReadLine()
  Invoke-Diag -Label "test-d-uu-drag-select" -Seconds 10
} else {
  Write-Host "Skipped interactive UU tests (SkipInteractiveUuTests)."
}

if (-not $SkipConsolePhysical) {
  Write-Host "TEST G: Console physical mouse on the controlled PC (NOT UU)."
  Write-Host "If you cannot operate the local console mouse, type NOT_RUN and Enter."
  Write-Host "Otherwise press Enter and use the LOCAL physical mouse for 8s."
  $ans = Read-Host
  if ($ans -eq "NOT_RUN") {
    $dir = Join-Path $OutRoot ("{0}-test-g-console-physical-NOT_RUN" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    '{"status":"NOT_RUN","reason":"cannot operate console physical mouse on controlled PC"}' |
      Set-Content -Encoding utf8 (Join-Path $dir "result.json")
    Write-Host "TEST G marked NOT_RUN -> $dir"
  } else {
    Invoke-Diag -Label "test-g-console-physical" -Seconds 8
  }
} else {
  Write-Host "Skipped TEST G."
}

Write-Host "Generating summary report..."
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "summarize-uu-remote-input-diagnostics.ps1") -OutRoot $OutRoot -ControllerPlatform $ControllerPlatform
Write-Host "Done. Evidence under $OutRoot"
