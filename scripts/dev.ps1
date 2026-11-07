#requires -Version 5.1
[CmdletBinding()]
param(
    [int]$Port = 5240,
    [switch]$Demo
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CXAudioOutput\CXAudioOutput.csproj'

$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (Test-Path $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $root '.nuget'
} elseif ($dotnetCommand) {
    $dotnet = $dotnetCommand.Source
} else {
    throw '找不到 dotnet。请安装 .NET 8 SDK。'
}

$arguments = @('run', '--project', $project, '--', '--port', $Port)
if ($Demo) { $arguments += '--demo' }

& $dotnet @arguments
