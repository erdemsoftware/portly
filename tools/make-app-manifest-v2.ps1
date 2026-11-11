<#
  Fills the "assets" of an app.json from the release files of a local dist\ folder (SHA-256 of every file) and writes
  the result and one merged SHA256SUMS (the file tools\install.sh verifies). It does NOT sign and does NOT touch
  manifest\app.json: review <Out>, copy it to manifest\app.json, then let sign-manifests sign it
  (docs\06-self-update.md has the format).

  Files looked for in -Dist (names as written by tools\package-unix.sh and the Windows build):
    Portly-<v>-windows-<x64|arm64>.zip            Windows zip: Portly.exe (and the rest of the program), lang\, templates\ (same layout as the 1.x zip)
    Portly-<v>-macos-<x64|arm64>.zip              macOS: Portly.app, zipped with ditto --keepParent
    Portly-<v>-linux-<x64|arm64>.tar.gz | .AppImage | .deb

  The top-level url / sha256 (read by Portly 1.x) become the Windows x64 zip. When there is no windows-x64 zip yet, the
  1.x file Portly-<v>-win10-11.zip is used; with neither, the script stops (a 1.x client would be offered nothing valid).
  "notes" and "donateUrl" are kept from -Manifest unless -NotesEn / -NotesTr are given.

  Usage:
    pwsh tools\make-app-manifest-v2.ps1 -Version 2.0.1 [-Dist dist] [-Manifest manifest\app.json] [-Out dist\app.json] [-NotesEn "..."] [-NotesTr "..."]
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Dist,
    [string]$Manifest,
    [string]$Out,
    [string]$BaseUrl,
    [string]$NotesEn,
    [string]$NotesTr
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Dist) { $Dist = Join-Path $here '..\dist' }
if (-not $Manifest) { $Manifest = Join-Path $here '../manifest/app.json' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 2.0.1" }
$Dist = (Resolve-Path $Dist).Path
if (-not $Out) { $Out = Join-Path $Dist 'app.json' }
if (-not $BaseUrl) { $BaseUrl = "https://github.com/erdemsoftware/portly/releases/download/v$Version" }
if (-not $BaseUrl.StartsWith('https://')) { throw "BaseUrl must be https" }

function New-Asset([string]$name) {
    $file = Join-Path $Dist $name
    if (-not (Test-Path $file)) { return $null }
    $item = Get-Item $file
    [ordered]@{ url = "$BaseUrl/$name"; sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower(); size = [long]$item.Length }
}

$assets = [ordered]@{}
foreach ($arch in 'x64', 'arm64') {
    $a = New-Asset "Portly-$Version-windows-$arch.zip"
    if ($a) { $assets["windows-$arch"] = $a }
}
foreach ($arch in 'arm64', 'x64') {
    $a = New-Asset "Portly-$Version-macos-$arch.zip"
    if ($a) { $assets["macos-$arch"] = $a }
}
foreach ($arch in 'x64', 'arm64') {
    $kinds = [ordered]@{}
    foreach ($k in @(@('tarball', 'tar.gz'), @('appimage', 'AppImage'), @('deb', 'deb'))) {
        $a = New-Asset "Portly-$Version-linux-$arch.$($k[1])"
        if ($a) { $kinds[$k[0]] = $a }
    }
    if ($kinds.Count -gt 0) { $assets["linux-$arch"] = $kinds }
}
if ($assets.Count -eq 0) { throw "No Portly-$Version-* release files in $Dist" }

# top level: what Portly 1.x reads
if ($assets.Contains('windows-x64')) { $top = $assets['windows-x64'] }
else {
    $top = New-Asset "Portly-$Version-win10-11.zip"
    if (-not $top) { throw "No Windows zip for $Version in ${Dist}: expected Portly-$Version-windows-x64.zip (or the 1.x name Portly-$Version-win10-11.zip)" }
}

$old = Get-Content $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$notes = [ordered]@{}
if ($old.notes) { foreach ($p in $old.notes.PSObject.Properties) { $notes[$p.Name] = $p.Value } }
if ($NotesEn) { $notes['en'] = $NotesEn }
if ($NotesTr) { $notes['tr'] = $NotesTr }
$donate = if ($old.PSObject.Properties['donateUrl']) { [string]$old.donateUrl } else { '' }

$app = [ordered]@{
    version = $Version
    url = $top.url
    sha256 = $top.sha256
    assets = $assets
    notes = $notes
    donateUrl = $donate
}
$json = ($app | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($Out, $json + "`n", $utf8)
$check = Get-Content $Out -Raw -Encoding UTF8 | ConvertFrom-Json
if ($check.version -ne $Version) { throw "Written file does not parse back" }
Write-Host "Wrote $Out ($($assets.Count) platforms)" -ForegroundColor Green

# one SHA256SUMS for the whole release (sha256sum format, two spaces), version-less and versioned names alike
$sums = Get-ChildItem $Dist -File -Filter 'Portly-*' | Sort-Object Name | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
[IO.File]::WriteAllText((Join-Path $Dist 'SHA256SUMS'), (($sums -join "`n") + "`n"), $utf8)
Write-Host "Wrote $(Join-Path $Dist 'SHA256SUMS') ($(@($sums).Count) files)" -ForegroundColor Green
