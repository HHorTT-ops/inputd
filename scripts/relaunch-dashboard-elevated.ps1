param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\inputd-dashboard-win-x64')
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if (-not $resolvedOutput.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must remain inside the workspace: $workspace"
}

Get-Process -Name 'inputd-dashboard' -ErrorAction SilentlyContinue | ForEach-Object {
    $process = $_
    $null = $process.CloseMainWindow()
    try { $process | Wait-Process -Timeout 10 -ErrorAction Stop }
    catch { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}

& (Join-Path $PSScriptRoot 'publish-dashboard.ps1') -OutputPath $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw "Dashboard publish failed with exit code $LASTEXITCODE." }

$dashboard = Join-Path $resolvedOutput 'inputd-dashboard.exe'
Start-Process -FilePath $dashboard
