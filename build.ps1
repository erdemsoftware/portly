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

# Optional code signing. Without these settings nothing is signed and the build works as before.
#   Certificate in the Windows certificate store (Certum SimplySign cloud key, a USB token ...):
#     PORTLY_SIGN_THUMBPRINT = the certificate's SHA1 thumbprint (SimplySign Desktop must be logged in)
#   Or a .pfx file (CI secrets): PORTLY_SIGN_PFX + PORTLY_SIGN_PASSWORD
#   PORTLY_SIGN_TIMESTAMP overrides the timestamp server (default: Certum's for a thumbprint, DigiCert's for a .pfx)
function Sign-Args {
    if ($env:PORTLY_SIGN_THUMBPRINT) {
        $ts = if ($env:PORTLY_SIGN_TIMESTAMP) { $env:PORTLY_SIGN_TIMESTAMP } else { 'http://time.certum.pl' }
        return @('/sha1', $env:PORTLY_SIGN_THUMBPRINT.Replace(' ', ''), '/fd', 'SHA256', '/tr', $ts, '/td', 'SHA256', '/d', 'Portly')
    }
    if ($env:PORTLY_SIGN_PFX -and (Test-Path $env:PORTLY_SIGN_PFX)) {
        $ts = if ($env:PORTLY_SIGN_TIMESTAMP) { $env:PORTLY_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
        return @('/f', $env:PORTLY_SIGN_PFX, '/p', $env:PORTLY_SIGN_PASSWORD, '/fd', 'SHA256', '/tr', $ts, '/td', 'SHA256', '/d', 'Portly')
    }
    return $null
}

$signArgs = Sign-Args
$signtool = $null
if ($signArgs) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found: install the Windows SDK (signing tools)" }
}

function Sign-File([string]$file) {
    if (-not $signArgs) { return }
    & $signtool.FullName sign @signArgs $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $file" }
    Write-Host "  signed $file" -ForegroundColor Green
}

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
    Sign-File (Join-Path $out 'Portly.exe')
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

    $iscc = foreach ($v in '7', '6') { "${env:ProgramFiles(x86)}\Inno Setup $v\ISCC.exe", "$env:ProgramFiles\Inno Setup $v\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup $v\ISCC.exe" }
    $iscc = $iscc | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        # with signing on, Inno Setup signs the uninstaller inside the setup too ($f = the file, $q = a quote)
        $isccSign = @()
        if ($signArgs) {
            $quoted = ($signArgs | ForEach-Object { if ($_ -match '\s') { '$q' + $_ + '$q' } else { $_ } }) -join ' '
            $isccSign = @('/DSign=1', "/Sportly=`$q$($signtool.FullName)`$q sign $quoted `$q`$f`$q")
        }
        & $iscc /Q @isccSign "/DAppVersion=$Version" "/DEdition=$($e.Suffix)" "/DSourceDir=$stage" "/DOutputDir=$dist" (Join-Path $root 'installer\portly.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer failed: $($e.Name)" }
        $setup = Join-Path $dist "Portly-$Version-$($e.Suffix)-setup.exe"
        Sign-File $setup
        $sums += "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())  $(Split-Path $setup -Leaf)"
        Write-Host "  $setup" -ForegroundColor Green
    }
    if ($e.Name -eq 'Modern') { $modernHash = $hash }
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

# winget / Scoop manifests with this version's URLs and hashes (build\manifests, not uploaded as release assets)
& (Join-Path $root 'tools\make-package-manifests.ps1') -Version $Version -Dist $dist -Out (Join-Path $root 'build\manifests')

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
