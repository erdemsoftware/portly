<#
  Commits manifest\ with matching signatures to main; used by the release, sign-manifests and fill-package-hashes
  workflows. Signatures are made on the newest main, right before the commit, and main may move until the push lands,
  so every try starts again from origin/main. Fails rather than pushing a manifest whose committed bytes do not verify.
  Needs PORTLY_MANIFEST_KEY. Pushing with the workflow's GITHUB_TOKEN starts no other workflow, so this cannot loop.

    tools\push-signed-manifests.ps1 -Message "Release v1.3.0: update manifest/app.json" -AppJson <file>
      -AppJson     replaces manifest\app.json first
      -HashState   applies the hashes found by tools\fill-package-hashes.ps1 first
#>
param(
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$AppJson = '',
    [switch]$HashState
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
Set-Location (Split-Path $tools)

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" }
}

Invoke-Git config user.name "github-actions"
Invoke-Git config user.email "github-actions@users.noreply.github.com"
$tries = 4
for ($try = 1; $try -le $tries; $try++) {
    Invoke-Git fetch origin main
    Invoke-Git checkout -f -B main origin/main
    if ($AppJson) { Copy-Item $AppJson manifest/app.json }
    if ($HashState) { & "$tools\fill-package-hashes.ps1" -StateOnly }
    & "$tools\sign-manifests.ps1"
    Invoke-Git add manifest
    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { Write-Host "Manifests and signatures are up to date"; return }
    Invoke-Git commit -m $Message
    & "$tools\sign-manifests.ps1" -VerifyCommitted
    & git push origin main
    if ($LASTEXITCODE -eq 0) { return }
    Write-Host "Push rejected ($try of $tries), trying again on the newest main" -ForegroundColor Yellow
    Start-Sleep -Seconds (5 * $try)
}
throw "Could not push the signed manifests to main"
