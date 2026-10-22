<#
  Portly build script
  -------------------
  Builds both editions and creates the release zips in dist\:
    Portly-<version>-win10.zip   Windows 10 / 11 (updates)
    Portly-<version>-win7.zip    Windows 7 / 8 / 8.1 (Legacy, frozen)
  and, when Inno Setup 6 is installed, the setup programs.

  Usage:
    powershell -ExecutionPolicy Bypass -File build.ps1                 # version from Portly.csproj
    powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.1  # set a version
    powershell -ExecutionPolicy Bypass -File build.ps1 -UpdateManifest # also write manifest\app.json
#>
param(
    [string]$Version = "",
    [switch]$UpdateManifest
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\Portly\Portly.csproj'

if ($Version -eq '') {
    $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Portly $Version" -ForegroundColor Cyan

# pick a dotnet that really has an SDK (a runtime-only dotnet.exe may come first in PATH)
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'), "$env:ProgramFiles\dotnet\dotnet.exe") | Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) } | Select-Object -First 1
if (-not $dotnet) { throw ".NET SDK not found. Install it from https://dot.net (any SDK 6+ can build .NET Framework 4.8 apps)." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$editions = @(
    @{ Name = 'Modern'; Suffix = 'win10' },
    @{ Name = 'Legacy'; Suffix = 'win7' }
)
$sums = @()
foreach ($e in $editions) {
    Write-Host "Building $($e.Name)..." -ForegroundColor Cyan
    & $dotnet build $proj -c Release -p:Edition=$($e.Name) -p:Version=$Version -t:Rebuild --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($e.Name)" }

    $out = Join-Path $root "build\$($e.Name)"
    $stage = Join-Path $root "build\stage\Portly"
    if (Test-Path (Split-Path $stage)) { Remove-Item -Recurse -Force (Split-Path $stage) }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item (Join-Path $out 'Portly.exe'), (Join-Path $out 'Portly.exe.config') $stage
    Copy-Item -Recurse (Join-Path $out 'lang'), (Join-Path $out 'templates') $stage
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
    Copy-Item (Join-Path $root "installer\README-$($e.Suffix).txt") (Join-Path $stage 'README.txt')

    $zip = Join-Path $dist "Portly-$Version-$($e.Suffix).zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $stage), $zip, 'Optimal', $false)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    $sums += "$hash  $(Split-Path $zip -Leaf)"
    Write-Host "  $zip" -ForegroundColor Green

    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc /Q "/DAppVersion=$Version" "/DEdition=$($e.Suffix)" "/DSourceDir=$stage" "/DOutputDir=$dist" (Join-Path $root 'installer\portly.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer failed: $($e.Name)" }
        $setup = Join-Path $dist "Portly-$Version-$($e.Suffix)-setup.exe"
        $sums += "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())  $(Split-Path $setup -Leaf)"
        Write-Host "  $setup" -ForegroundColor Green
    }
    if ($e.Name -eq 'Modern') { $modernHash = $hash }
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

if ($UpdateManifest) {
    $manifest = Join-Path $root 'manifest\app.json'
    $app = Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $app.version = $Version
    $app.url = "https://github.com/erdemsoftware/portly/releases/download/v$Version/Portly-$Version-win10.zip"
    $app.sha256 = $modernHash
    ($app | ConvertTo-Json -Depth 5) | Set-Content $manifest -Encoding utf8
    Write-Host "manifest\app.json updated (commit it AFTER the GitHub release is published)" -ForegroundColor Yellow
}
Write-Host "Done." -ForegroundColor Cyan
