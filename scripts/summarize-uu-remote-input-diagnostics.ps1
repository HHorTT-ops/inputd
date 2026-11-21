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
  $verdictPath = Join-Path $run.FullName "stimulus-verdict.json"
  $coordPath = Join-Path $run.FullName "coordinate-diagnostics.json"
  $comparePath = Join-Path $run.FullName "device-compare.json"
  $item = [ordered]@{
    folder = $run.Name
    meta = $(if (Test-Path $metaPath) { Get-Content $metaPath -Raw | ConvertFrom-Json } else { $null })
    stats = $(if (Test-Path $statsPath) { Get-Content $statsPath -Raw | ConvertFrom-Json } else { $null })
    correlation_answers = $(if (Test-Path $corrPath) { (Get-Content $corrPath -Raw | ConvertFrom-Json).answers_preliminary } else { $null })
    correlation_counts = $(if (Test-Path $corrPath) {
      $c = Get-Content $corrPath -Raw | ConvertFrom-Json
      [ordered]@{ matched = $c.matched_one_to_one; ambiguous = $c.ambiguous; unmatched_raw = $c.unmatched_raw }
    } else { $null })
    stimulus_verdict = $(if (Test-Path $verdictPath) { Get-Content $verdictPath -Raw | ConvertFrom-Json } else { $null })
    has_coordinate_diagnostics = (Test-Path $coordPath)
    has_device_compare = (Test-Path $comparePath)
    session_sm_remote = $(if (Test-Path $envPath) { (Get-Content $envPath -Raw | ConvertFrom-Json).sm_remote_session } else { $null })
    not_run = $(if (Test-Path $notRun) { Get-Content $notRun -Raw | ConvertFrom-Json } else { $null })
  }
  $summaries += $item
}

$report = [ordered]@{
  title = "UU Remote Input Diagnostics Summary"
  task = "INPUTD-PHASE1.2-DPI-COORDINATE-ALIGNMENT-001"
  controller_platform = $ControllerPlatform
  generated_at = (Get-Date).ToString("o")
  source_classes_unchanged = @("EXECUTOR_INJECTED", "DEVICE_ATTRIBUTED", "UNATTRIBUTED")
  external_input_activity_note = "Any non-EXECUTOR_INJECTED external input is a conservative pause candidate for Qwen; do not claim it is human."
  sm_remote_session_caveat = "SM_REMOTESESSION=false does not prove absence of NetEase UU / third-party remote control."
  coordinate_note = "Raw metrics-space vs WH_MOUSE_LL may differ under DPI virtualization; alignment uses measured scale / LogicalToPhysical — never hardcoded 2x."
  correlation_note = "Prior Founder captures had absolute moves classified as other and unmatched; Phase 1.2 treats absolute zero-delta as move and aligns coords before match."
  phase12_status = [ordered]@{
    dpi_diagnostics = "implemented"
    absolute_mapping_alignment = "implemented"
    correlation_fix = "implemented (unit-tested); Founder UU retest pending"
    manual_test_prompts = "hardened A/B-left/B-right/C-v/C-h/D/G"
    device_compare = "implemented"
    founder_retest = "PENDING"
  }
  runs = $summaries
}
$outFile = Join-Path $OutRoot ("SUMMARY-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
($report | ConvertTo-Json -Depth 12) | Set-Content -Encoding utf8 $outFile
Write-Host "Summary: $outFile"
