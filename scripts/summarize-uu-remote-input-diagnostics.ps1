param(
  [Parameter(Mandatory = $true)][string]$OutRoot,
  [string]$ControllerPlatform = "UNKNOWN"
)

$ErrorActionPreference = "Stop"
$runs = Get-ChildItem $OutRoot -Directory | Sort-Object Name
$summaries = @()
foreach ($run in $runs) {
  $statsPath = Join-Path $run.FullName "channel-stats.json"
  $corrPath = Join-Path $run.FullName "correlation-report.json"
  $envPath = Join-Path $run.FullName "session-environment.json"
  $metaPath = Join-Path $run.FullName "run-meta.json"
  $notRun = Join-Path $run.FullName "result.json"
  $item = [ordered]@{
    folder = $run.Name
    meta = $(if (Test-Path $metaPath) { Get-Content $metaPath -Raw | ConvertFrom-Json } else { $null })
    stats = $(if (Test-Path $statsPath) { Get-Content $statsPath -Raw | ConvertFrom-Json } else { $null })
    correlation_answers = $(if (Test-Path $corrPath) { (Get-Content $corrPath -Raw | ConvertFrom-Json).answers_preliminary } else { $null })
    session_sm_remote = $(if (Test-Path $envPath) { (Get-Content $envPath -Raw | ConvertFrom-Json).sm_remote_session } else { $null })
    not_run = $(if (Test-Path $notRun) { Get-Content $notRun -Raw | ConvertFrom-Json } else { $null })
  }
  $summaries += $item
}

$report = [ordered]@{
  title = "UU Remote Input Diagnostics Summary"
  controller_platform = $ControllerPlatform
  generated_at = (Get-Date).ToString("o")
  source_classes_unchanged = @("EXECUTOR_INJECTED", "DEVICE_ATTRIBUTED", "UNATTRIBUTED")
  external_input_activity_note = "Any non-EXECUTOR_INJECTED external input is a conservative pause candidate for Qwen; do not claim it is human."
  sm_remote_session_caveat = "SM_REMOTESESSION=false does not prove absence of NetEase UU / third-party remote control."
  runs = $summaries
}
$outFile = Join-Path $OutRoot ("SUMMARY-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
($report | ConvertTo-Json -Depth 10) | Set-Content -Encoding utf8 $outFile
Write-Host "Summary: $outFile"
