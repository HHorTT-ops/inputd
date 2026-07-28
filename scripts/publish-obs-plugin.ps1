param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\inputd-obs-plugin')
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputPath)
$bridgeOutput = Join-Path $output 'bridge'

dotnet publish (Join-Path $workspace 'src\InputExecutor.ObsBridge\InputExecutor.ObsBridge.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $bridgeOutput
if ($LASTEXITCODE -ne 0) { throw "OBS bridge publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $workspace 'adapters\obs-inputd-plugin\inputd-obs.lua') `
    -Destination (Join-Path $output 'inputd-obs.lua') -Force
Copy-Item -LiteralPath (Join-Path $workspace 'adapters\obs-inputd-plugin\README.md') `
    -Destination (Join-Path $output 'README.md') -Force

Write-Host "Published OBS plugin package: $output"
