<#
  Fills the winget and Scoop templates in manifests\ with a release's version, download URLs and SHA-256.
  Called by build.ps1 (and so by the release workflow); can also be run alone:
    powershell -ExecutionPolicy Bypass -File tools\make-package-manifests.ps1 -Version 1.0.1 -Dist dist -Out build\manifests
  Output:
    <Out>\winget\manifests\e\ErdemSoftware\Portly\<version>\*.yaml   (copy into a fork of microsoft/winget-pkgs)
    <Out>\scoop\portly.json                                           (copy into the Scoop bucket repository)
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Dist = (Join-Path $PSScriptRoot '..\dist'),
    [string]$Out = (Join-Path $PSScriptRoot '..\build\manifests')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$base = "https://github.com/erdemsoftware/portly/releases/download/v$Version"

function Hash([string]$file) { if (Test-Path $file) { (Get-FileHash $file -Algorithm SHA256).Hash } else { $null } }

$setup = Join-Path $Dist "Portly-$Version-win10-setup.exe"
$zip = Join-Path $Dist "Portly-$Version-win10.zip"
$setupHash = Hash $setup
$zipHash = Hash $zip

if ($setupHash) {
    $wingetOut = Join-Path $Out "winget\manifests\e\ErdemSoftware\Portly\$Version"
    New-Item -ItemType Directory -Force $wingetOut | Out-Null
    foreach ($t in Get-ChildItem (Join-Path $root 'manifests\winget') -Filter *.yaml) {
        $text = Get-Content $t.FullName -Raw -Encoding UTF8
        $text = $text.Replace('{{VERSION}}', $Version).Replace('{{URL}}', "$base/Portly-$Version-win10-setup.exe").Replace('{{SHA256}}', $setupHash).Replace('{{DATE}}', (Get-Date -Format 'yyyy-MM-dd'))
        # drop the template note on the first line
        $text = ($text -split "`r?`n" | Where-Object { $_ -notmatch '^# Template' }) -join "`n"
        [IO.File]::WriteAllText((Join-Path $wingetOut $t.Name), $text, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Host "  winget manifests: $wingetOut" -ForegroundColor Green
} else { Write-Host "  winget skipped: $setup not found (Inno Setup missing?)" -ForegroundColor Yellow }

if ($zipHash) {
    $scoopOut = Join-Path $Out 'scoop'
    New-Item -ItemType Directory -Force $scoopOut | Out-Null
    $text = Get-Content (Join-Path $root 'manifests\scoop\portly.json') -Raw -Encoding UTF8
    $text = $text.Replace('{{VERSION}}', $Version).Replace('{{ZIP_URL}}', "$base/Portly-$Version-win10.zip").Replace('{{ZIP_SHA256}}', $zipHash.ToLower())
    [IO.File]::WriteAllText((Join-Path $scoopOut 'portly.json'), $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "  Scoop manifest: $scoopOut\portly.json" -ForegroundColor Green
} else { Write-Host "  Scoop skipped: $zip not found" -ForegroundColor Yellow }
