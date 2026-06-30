#requires -Version 5
# Local launcher for CloudAgent. Loads secrets from agent.secrets.ps1 (if present),
# turns on the local web chat UI, and runs the host.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$secrets = Join-Path $PSScriptRoot "agent.secrets.ps1"
if (Test-Path $secrets) {
    . $secrets
} else {
    Write-Host "No agent.secrets.ps1 found - running offline on the echo provider." -ForegroundColor Yellow
    Write-Host "Copy agent.secrets.example.ps1 to agent.secrets.ps1 and add your keys for a real model." -ForegroundColor Yellow
}

# Local web chat UI on by default.
if (-not $env:WEBCHAT)      { $env:WEBCHAT = "1" }
if (-not $env:WEBCHAT_PORT) { $env:WEBCHAT_PORT = "5080" }

Write-Host "CloudAgent starting - web chat at http://localhost:$($env:WEBCHAT_PORT)/" -ForegroundColor Cyan
dotnet run --project src/CloudAgent.Host -c Release
