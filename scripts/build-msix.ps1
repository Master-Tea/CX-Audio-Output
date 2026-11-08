#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PythonPath = '',
    [string]$PfxPassword = 'CxAudio-MSIX-2026!',
    [switch]$SkipBuild,
    [switch]$SkipAssets,
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CXAudioOutput\CXAudioOutput.csproj'
$dist = Join-Path $root 'dist\CXAudioOutput-win-x64'
$manifest = Join-Path $root 'packaging\msix\AppxManifest.xml'
$assets = Join-Path $root 'packaging\msix\Assets'
$pfx = Join-Path $root 'packaging\msix\signing\MasterTea-CXAudioOutput.pfx'
$staging = Join-Path $root '.tmp\msix-staging'

if (-not (Test-Path $manifest)) { throw "Manifest not found: $manifest" }
if (-not (Test-Path $pfx)) { throw "Signing certificate not found: $pfx" }

$winapp = (Get-Command winapp -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $winapp) {
    $winapp = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winapp.exe'
}
if (-not (Test-Path $winapp)) {
    throw 'winapp CLI not found. Install Windows App SDK / winapp, or add it to PATH.'
}

if (-not $SkipBuild) {
    Write-Host 'Publishing self-contained app...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $dist)) {
    throw "Publish folder not found: $dist. Run build.ps1 first."
}

if (-not $SkipAssets) {
    if (-not $PythonPath) {
        $pythonCommand = Get-Command python -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pythonCommand) {
            $PythonPath = $pythonCommand.Source
        } elseif (Test-Path (Join-Path $root '.tools\python\python.exe')) {
            $PythonPath = Join-Path $root '.tools\python\python.exe'
        } else {
            throw 'Python with Pillow is required to generate MSIX icons. Pass -PythonPath.'
        }
    }

    Write-Host "Generating MSIX icons with $PythonPath ..." -ForegroundColor Cyan
    & $PythonPath (Join-Path $PSScriptRoot 'make-msix-assets.py')
    if ($LASTEXITCODE -ne 0) { throw "make-msix-assets.py failed with exit code $LASTEXITCODE" }
}

[xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw
$version = $manifestXml.Package.Identity.Version
if (-not $version) { throw 'Could not read Identity/@Version from the manifest.' }
$output = Join-Path $root "dist\CXAudioOutput_${version}_x64.msix"

if (Test-Path $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $dist '*') -Destination $staging -Recurse -Force
Remove-Item -LiteralPath (Join-Path $staging 'data') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $manifest -Destination $staging -Force
Copy-Item -LiteralPath $assets -Destination $staging -Recurse -Force

Write-Host "Packaging $version ..." -ForegroundColor Cyan
& $winapp package $staging `
    --manifest (Join-Path $staging 'AppxManifest.xml') `
    --output $output `
    --cert $pfx `
    --cert-password $PfxPassword
if ($LASTEXITCODE -ne 0) { throw "winapp package failed with exit code $LASTEXITCODE" }

if (-not $NoTimestamp) {
    Write-Host 'Adding a timestamp signature...' -ForegroundColor Cyan
    & $winapp sign $output $pfx --password $PfxPassword --timestamp 'http://timestamp.digicert.com'
    if ($LASTEXITCODE -ne 0) { throw "winapp sign failed with exit code $LASTEXITCODE" }
}

Write-Host 'Verifying package signature...' -ForegroundColor Cyan
& $winapp tool signtool verify /pa $output
if ($LASTEXITCODE -ne 0) { throw "Signature verification failed with exit code $LASTEXITCODE" }

Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "MSIX created: $output" -ForegroundColor Green
Write-Host "Install with: Add-AppxPackage -Path `"$output`"" -ForegroundColor Green
