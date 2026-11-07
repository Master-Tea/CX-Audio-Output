#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [int]$Port = 5240
)

$ErrorActionPreference = 'Stop'
$name = "CX Audio Output (TCP $Port)"
$existing = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "防火墙规则已存在：$name" -ForegroundColor Yellow
    return
}

New-NetFirewallRule `
    -DisplayName $name `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort $Port `
    -Action Allow `
    -Profile Private | Out-Null

Write-Host "已添加防火墙规则：$name" -ForegroundColor Green
