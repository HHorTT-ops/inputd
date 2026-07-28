param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..\artifacts\inputd-obs-plugin'),
    [string]$InstallPath = (Join-Path $env:APPDATA 'obs-studio\scripts\inputd-obs'),
    [switch]$Register,
    [string]$SceneCollectionPath
)

$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackagePath)
$install = [IO.Path]::GetFullPath($InstallPath)
if (-not (Test-Path -LiteralPath (Join-Path $package 'inputd-obs.lua'))) {
    throw "Published OBS plugin package was not found at $package. Run publish-obs-plugin.ps1 first."
}

New-Item -ItemType Directory -Path $install -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $package 'inputd-obs.lua') -Destination (Join-Path $install 'inputd-obs.lua') -Force
Copy-Item -LiteralPath (Join-Path $package 'README.md') -Destination (Join-Path $install 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $package 'bridge') -Destination $install -Recurse -Force

Write-Host "Installed OBS script package: $install"
$scriptPath = Join-Path $install 'inputd-obs.lua'
$bridgePath = Join-Path $install 'bridge\inputd-obs-bridge.exe'
if ($Register) {
    if (Get-Process obs64 -ErrorAction SilentlyContinue) {
        throw 'Close OBS before using -Register so OBS cannot overwrite the updated script list.'
    }
    if ([string]::IsNullOrWhiteSpace($SceneCollectionPath)) {
        $globalIni = Join-Path $env:APPDATA 'obs-studio\global.ini'
        $sceneFile = (Get-Content -LiteralPath $globalIni | Select-String '^SceneCollectionFile=(.+)$').Matches.Groups[1].Value
        if ([string]::IsNullOrWhiteSpace($sceneFile)) { throw 'Could not determine the active OBS scene collection.' }
        $SceneCollectionPath = Join-Path $env:APPDATA "obs-studio\basic\scenes\$sceneFile.json"
    }
    & $bridgePath register --scene $SceneCollectionPath --script $scriptPath --bridge $bridgePath
    if ($LASTEXITCODE -ne 0) { throw "OBS script registration failed with exit code $LASTEXITCODE." }
    Write-Host "Registered OBS script in: $SceneCollectionPath"
} else {
    Write-Host "Add this file once in OBS: $scriptPath"
}
