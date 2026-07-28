param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\inputd-dashboard-win-x64')
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Publish-Project([string]$ProjectPath) {
    dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false -o $resolvedOutput
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath. Close inputd/inputd-dashboard processes that are using the output directory and retry."
    }
}

Publish-Project (Join-Path $workspace 'src\InputExecutor.Dashboard\InputExecutor.Dashboard.csproj')
Publish-Project (Join-Path $workspace 'src\InputExecutor.Service\InputExecutor.Service.csproj')
Publish-Project (Join-Path $workspace 'src\InputExecutor.Cli\InputExecutor.Cli.csproj')

Write-Host "Dashboard bundle published to: $resolvedOutput"
Write-Host "Launch: $(Join-Path $resolvedOutput 'inputd-dashboard.exe')"
