#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CXAudioOutput\CXAudioOutput.csproj'
$output = Join-Path $root 'dist\CXAudioOutput-win-x64'

$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (Test-Path $localDotnet) {
    $dotnet = $localDotnet
} elseif ($dotnetCommand) {
    $dotnet = $dotnetCommand.Source
} else {
    throw '找不到 dotnet。请安装 .NET 8 SDK，或把 SDK 放在 .tools\dotnet\。'
}

if (Test-Path $localDotnet) {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $root '.nuget'
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null
}

Write-Host "Using $dotnet" -ForegroundColor DarkGray
if (Test-Path $output) {
    Write-Host "Cleaning $output" -ForegroundColor DarkGray
    Remove-Item -LiteralPath $output -Recurse -Force
}

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishTrimmed=false `
    -o $output `
    -v minimal

if ($LASTEXITCODE -ne 0) {
    throw "发布失败（退出码 $LASTEXITCODE）。"
}

Copy-Item (Join-Path $root 'packaging\Start CX Audio Output.cmd') $output -Force
Copy-Item (Join-Path $root 'packaging\README.txt') $output -Force
Remove-Item (Join-Path $output 'web.config') -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "构建完成：$output" -ForegroundColor Green
Write-Host "双击 CXAudioOutput.exe 或 Start CX Audio Output.cmd 启动。" -ForegroundColor Green
