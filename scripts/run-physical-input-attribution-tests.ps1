<#
.SYNOPSIS
  Attribution / stimulus helpers for physical-input SSE.

.PARAMETER Mode
  listen              - connect SSE only (optionally with hand stimulus)
  hand                - listen while Founder operates a real physical mouse
  untagged-mouse-event - inject untagged user32.mouse_event during listen
  tagged-sendinput    - inject SendInput with inputd ExecutorEventTag during listen

.NOTES
  Hand modes MUST be operated by a real physical mouse. Do not use injection to fake PASS.
#>
param(
  [ValidateSet("listen", "hand", "untagged-mouse-event", "tagged-sendinput")]
  [string]$Mode = "listen",
  [string]$BaseUrl = "http://127.0.0.1:17841",
  [int]$Seconds = 10,
  [string]$EvidenceLabel = ""
)
$ErrorActionPreference = "Stop"
$ExecutorEventTag = [uint64]0x494E505554440001
$tokenPath = Join-Path $env:LOCALAPPDATA "inputd\access.token"
$outDir = Join-Path $PSScriptRoot "..\docs\physical-input-sse"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Ensure-Inputd {
  try {
    Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2 | Out-Null
    return $null
  } catch {
    $exeCandidates = @(
      (Join-Path $PSScriptRoot "..\src\InputExecutor.Service\bin\Release\net10.0-windows\inputd.exe"),
      (Join-Path $PSScriptRoot "..\src\InputExecutor.Service\bin\Release\net10.0-windows\win-x64\inputd.exe")
    )
    $exe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) { throw "Build Release first; inputd.exe not found." }
    Write-Host "Starting $exe"
    $p = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    return $p
  }
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class InputStimulus {
  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public INPUTUNION U; }
  [StructLayout(LayoutKind.Explicit)]
  public struct INPUTUNION {
    [FieldOffset(0)] public MOUSEINPUT mi;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT {
    public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
  }
  public const uint INPUT_MOUSE = 0;
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_WHEEL = 0x0800;
  [DllImport("user32.dll", SetLastError = true)]
  public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
  [DllImport("user32.dll")]
  public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

  public static void UntaggedMouseEventBurst() {
    // ExtraInformation = 0 �?must classify as UNATTRIBUTED when seen on Raw Input.
    mouse_event(MOUSEEVENTF_MOVE, 20, 0, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_MOVE, 0, 15, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)120), UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_MOVE, unchecked((uint)(-20)), unchecked((uint)(-15)), 0, UIntPtr.Zero);
  }

  public static void TaggedSendInputBurst(ulong tag) {
    var inputs = new INPUT[6];
    for (int i = 0; i < inputs.Length; i++) {
      inputs[i].type = INPUT_MOUSE;
      inputs[i].U.mi.dwExtraInfo = new UIntPtr(tag);
    }
    inputs[0].U.mi.dwFlags = MOUSEEVENTF_MOVE; inputs[0].U.mi.dx = 25; inputs[0].U.mi.dy = 0;
    inputs[1].U.mi.dwFlags = MOUSEEVENTF_MOVE; inputs[1].U.mi.dx = 0; inputs[1].U.mi.dy = 20;
    inputs[2].U.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
    inputs[3].U.mi.dwFlags = MOUSEEVENTF_LEFTUP;
    inputs[4].U.mi.dwFlags = MOUSEEVENTF_WHEEL; inputs[4].U.mi.mouseData = 120;
    inputs[5].U.mi.dwFlags = MOUSEEVENTF_MOVE; inputs[5].U.mi.dx = -25; inputs[5].U.mi.dy = -20;
    if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
      throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  }
}
"@

function Show-HandInstructionWindow([string]$text) {
  Add-Type -AssemblyName System.Windows.Forms | Out-Null
  Add-Type -AssemblyName System.Drawing | Out-Null
  $form = New-Object System.Windows.Forms.Form
  $form.Text = "inputd physical-input SSE �?hand test"
  $form.Width = 520
  $form.Height = 260
  $form.TopMost = $true
  $form.StartPosition = "CenterScreen"
  $label = New-Object System.Windows.Forms.Label
  $label.Dock = "Fill"
  $label.Font = New-Object System.Drawing.Font("Segoe UI", 12)
  $label.Text = $text
  $label.Padding = New-Object System.Windows.Forms.Padding(16)
  $form.Controls.Add($label)
  $form.Show()
  return $form
}

$proc = Ensure-Inputd
if (-not (Test-Path $tokenPath)) { throw "Missing access token at $tokenPath" }
$token = (Get-Content $tokenPath -Raw).Trim()

$handForm = $null
if ($Mode -eq "hand") {
  $handForm = Show-HandInstructionWindow @"
请在倒计时内用【真实物理鼠标】完成：
1) 移动光标若干�?
2) 左键单击至少一�?
3) 滚动滚轮至少一�?

不要用其他程序注入输入�?
窗口可保持置顶；完成后无需点按钮，脚本会自动结束�?
"@
}

Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMilliseconds(-1)
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$BaseUrl/v1/physical-input/events")
$req.Headers.Add("X-Inputd-Access-Token", $token)
Write-Host ("Mode={0} listening {1}s" -f $Mode, $Seconds)
$cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Seconds))
$resp = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cts.Token).GetAwaiter().GetResult()
Write-Host ("HTTP {0} {1}" -f [int]$resp.StatusCode, $resp.Content.Headers.ContentType)
if (-not $resp.IsSuccessStatusCode) { throw "SSE request failed" }

$stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
$reader = New-Object System.IO.StreamReader($stream)
$mouse = 0; $heartbeat = 0; $fault = 0
$byClass = @{ DEVICE_ATTRIBUTED = 0; UNATTRIBUTED = 0; OTHER = 0 }
$maxTagged = 0
$samples = New-Object System.Collections.Generic.List[string]
$eventName = $null
$stimulusStarted = $false
$stimulusBurstsLeft = 0
$nextStimulusUtc = [datetime]::UtcNow.AddMilliseconds(800)
if ($Mode -eq "untagged-mouse-event" -or $Mode -eq "tagged-sendinput") {
  $stimulusBurstsLeft = 4
}
try {
  while (-not $cts.IsCancellationRequested) {
    $lineTask = $reader.ReadLineAsync()
    while (-not $lineTask.IsCompleted) {
      if ($cts.IsCancellationRequested) { break }
      if ($stimulusBurstsLeft -gt 0 -and [datetime]::UtcNow -ge $nextStimulusUtc) {
        if ($Mode -eq "untagged-mouse-event") {
          [InputStimulus]::UntaggedMouseEventBurst()
        } elseif ($Mode -eq "tagged-sendinput") {
          [InputStimulus]::TaggedSendInputBurst([uint64]0x494E505554440001)
        }
        $stimulusStarted = $true
        $stimulusBurstsLeft--
        $nextStimulusUtc = [datetime]::UtcNow.AddMilliseconds(400)
      }
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
        try {
          $obj = $data | ConvertFrom-Json
          $cls = [string]$obj.source_class
          if ($byClass.ContainsKey($cls)) { $byClass[$cls]++ } else { $byClass["OTHER"]++ }
        } catch { $byClass["OTHER"]++ }
        if ($samples.Count -lt 8) { [void]$samples.Add($data) }
      } elseif ($eventName -eq "heartbeat") {
        $heartbeat++
        try {
          $obj = $data | ConvertFrom-Json
          $t = [int64]$obj.tagged_frames_seen
          if ($t -gt $maxTagged) { $maxTagged = $t }
        } catch {}
      } elseif ($eventName -eq "fault") {
        $fault++
        [void]$samples.Add($data)
      }
      $eventName = $null
    }
  }
} catch [System.OperationCanceledException] {}

if ($handForm) { $handForm.Close(); $handForm.Dispose() }
Write-Host ("stimulus_started={0} bursts_remaining={1}" -f $stimulusStarted, $stimulusBurstsLeft)

$label = if ($EvidenceLabel) { $EvidenceLabel } else { $Mode }
$result = [ordered]@{
  mode = $Mode
  mouse_events = $mouse
  heartbeat_events = $heartbeat
  fault_events = $fault
  source_class_counts = $byClass
  max_tagged_frames_seen = $maxTagged
  sample_events = $samples
  seconds = $Seconds
  base_url = $BaseUrl
  hand_operated = ($Mode -eq "hand")
  recorded_at = (Get-Date).ToString("o")
  verdict_notes = $(
    switch ($Mode) {
      "hand" {
        if ($mouse -gt 0 -and $byClass["DEVICE_ATTRIBUTED"] -gt 0 -and $fault -eq 0) {
          "OBSERVED_DEVICE_ATTRIBUTED_EVENTS �?still requires human confirmation that only a physical mouse was used"
        } elseif ($mouse -gt 0) {
          "EVENTS_SEEN_BUT_NOT_DEVICE_ATTRIBUTED �?investigate hDevice"
        } else { "NO_MOUSE_EVENTS �?NOT_PASS" }
      }
      "untagged-mouse-event" {
        if ($byClass["UNATTRIBUTED"] -gt 0 -and $byClass["DEVICE_ATTRIBUTED"] -eq 0) {
          "PASS_UNATTRIBUTED_AS_EXPECTED"
        } elseif ($byClass["UNATTRIBUTED"] -gt 0) {
          "MIXED_CLASSES �?review samples; injection may coexist with real mouse motion"
        } else { "FAIL_EXPECTED_UNATTRIBUTED" }
      }
      "tagged-sendinput" {
        if ($mouse -eq 0 -and $maxTagged -gt 0) {
          "PASS_TAGGED_EXCLUDED_FROM_MOUSE_STREAM"
        } elseif ($mouse -gt 0) {
          "FAIL_TAGGED_OR_OTHER_INPUT_APPEARED_AS_MOUSE"
        } else { "FAIL_NO_TAGGED_COUNTER_INCREASE" }
      }
      default { "LISTEN_ONLY" }
    }
  )
}
$evidence = Join-Path $outDir ("attribution-" + $label + "-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
($result | ConvertTo-Json -Depth 6) | Set-Content -Encoding utf8 $evidence
Write-Host ($result | ConvertTo-Json -Depth 6)
Write-Host "Evidence: $evidence"
if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
