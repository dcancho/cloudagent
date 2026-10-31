#requires -Version 5
# Builds the host for linux-x64, copies it to /opt/cloudagent/<version> on the server,
# points /opt/cloudagent/current at it and restarts the service.
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = "ubuntu",
    [int]$Keep = 5
)
$ErrorActionPreference = "Stop"

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "publish\linux-x64"
$commit = (git -C $root rev-parse --short HEAD).Trim()
$version = "{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), $commit
$target = "$User@$Server"
$release = "/opt/cloudagent/$version"

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
Invoke-Native dotnet @("publish", (Join-Path $root "src\CloudAgent.Host"), "-c", "Release", "-r", "linux-x64", "--self-contained", "-o", $output)

$docs = Join-Path $output "docs"
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item (Join-Path $root "docs\consolidation-policy.md") $docs

Invoke-Native scp @("-r", $output, "${target}:$release")

$remote = @(
    "chmod -R u=rwX,go=rX $release",
    "chmod +x $release/CloudAgent.Host",
    "ln -sfn $release /opt/cloudagent/current",
    "sudo systemctl restart cloudagent",
    "find /opt/cloudagent -mindepth 1 -maxdepth 1 -type d | sort | head -n -$Keep | xargs -r rm -rf",
    "sudo systemctl status cloudagent --no-pager"
) -join " && "
Invoke-Native ssh @($target, $remote)

Write-Host "Deployed $version"
