param(
  [string]$BaseUrl = "http://127.0.0.1:17841",
  [int]$Seconds = 8
)
$ErrorActionPreference = "Stop"
$tokenPath = Join-Path $env:LOCALAPPDATA "inputd\access.token"
$outDir = Join-Path $PSScriptRoot "..\artifacts\physical-mouse-sse-smoke"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$startedHere = $false
$proc = $null
try {
  Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2 | Out-Null
} catch {
  $exeCandidates = @(
    (Join-Path $PSScriptRoot "..\src\InputExecutor.Service\bin\Release\net10.0-windows\inputd.exe"),
    (Join-Path $PSScriptRoot "..\src\InputExecutor.Service\bin\Release\net10.0-windows\win-x64\inputd.exe")
  )
  $exe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $exe) { throw "Build Release first; inputd.exe not found." }
  Write-Host "Starting $exe"
  $proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
  $startedHere = $true
  Start-Sleep -Seconds 2
}
if (-not (Test-Path $tokenPath)) { throw "Missing access token at $tokenPath" }
$token = (Get-Content $tokenPath -Raw).Trim()

Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMilliseconds(-1)
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$BaseUrl/v1/physical-input/events")
$req.Headers.Add("X-Inputd-Access-Token", $token)
Write-Host "Connecting for $Seconds seconds. Move/click/scroll the physical mouse now."
$cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Seconds))
$resp = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cts.Token).GetAwaiter().GetResult()
Write-Host ("HTTP {0} {1}" -f [int]$resp.StatusCode, $resp.Content.Headers.ContentType)
if (-not $resp.IsSuccessStatusCode) { throw "SSE request failed" }
$stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
$reader = New-Object System.IO.StreamReader($stream)
$mouse = 0; $heartbeat = 0; $fault = 0
$samples = New-Object System.Collections.Generic.List[string]
$eventName = $null
try {
  while (-not $cts.IsCancellationRequested) {
    $lineTask = $reader.ReadLineAsync()
    while (-not $lineTask.IsCompleted) {
      if ($cts.IsCancellationRequested) { break }
      Start-Sleep -Milliseconds 20
    }
    if ($cts.IsCancellationRequested) { break }
    $line = $lineTask.GetAwaiter().GetResult()
    if ($null -eq $line) { break }
    if ($line.StartsWith("event:")) { $eventName = $line.Substring(6).Trim() }
    elseif ($line.StartsWith("data:")) {
      $data = $line.Substring(5).Trim()
      if ($eventName -eq "mouse") {
        $mouse++
        if ($samples.Count -lt 5) { [void]$samples.Add($data) }
      } elseif ($eventName -eq "heartbeat") { $heartbeat++ }
      elseif ($eventName -eq "fault") { $fault++; [void]$samples.Add($data) }
      $eventName = $null
    }
  }
} catch [System.OperationCanceledException] {}

$result = [ordered]@{
  mouse_events = $mouse
  heartbeat_events = $heartbeat
  fault_events = $fault
  sample_mouse_or_fault = $samples
  seconds = $Seconds
  base_url = $BaseUrl
  recorded_at = (Get-Date).ToString("o")
}
$evidence = Join-Path $outDir ("smoke-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
($result | ConvertTo-Json -Depth 5) | Set-Content -Encoding utf8 $evidence
Write-Host ($result | ConvertTo-Json -Depth 5)
Write-Host "Evidence: $evidence"
if ($startedHere -and $proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
if ($mouse -gt 0 -and $fault -eq 0) { exit 0 } else { exit 3 }
