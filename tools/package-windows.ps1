<#
  Builds the Portly 2 package for Windows 10 / 11 into dist\ (the Windows counterpart of tools/package-unix.sh).
    pwsh tools\package-windows.ps1 [-Rid win-x64|win-arm64] [-Version 2.0.0]
  Result: dist\Portly-<v>-windows-<arch>.zip with a "Portly" folder (Portly.exe single file, lang\, templates\), the same
  layout as the 1.x zip so the in-app updater of either version can install it; a version-less copy
  (Portly-windows-<arch>.zip) and dist\SHA256SUMS (rewritten for every Portly-* file in dist\).
#>
param(
    [string]$Rid = 'win-x64',
    [string]$Version,
    # beta.1 -> file names and the label shown become 2.0.0-beta.1; the assembly version (updates compare it) stays 2.0.0
    [string]$Prerelease = $env:PORTLY_PRERELEASE
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root 'src\Portly.Desktop\Portly.Desktop.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 2.0.0 (got '$Version')" }
if ($Rid -notin 'win-x64', 'win-arm64') { throw "Rid must be win-x64 or win-arm64" }
$arch = $Rid.Substring(4)
if ($Prerelease -and $Prerelease -notmatch '^[a-z]+\.?\d+$') { throw "Prerelease must look like beta.1" }
$label = if ($Prerelease) { "$Version-$Prerelease" } else { $Version }

# the SDK on PATH may be a runtime only (see build.ps1): prefer the per-user SDK when it is there
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$pub = Join-Path $root "build\publish\$Rid"
$dist = Join-Path $root 'dist'
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
New-Item -ItemType Directory -Force $dist | Out-Null

& $dotnet publish $proj -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none "-p:Version=$label" -o $pub --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Get-ChildItem $pub -Recurse -Filter *.pdb | Remove-Item -Force
foreach ($need in 'Portly.exe', 'lang\en.json', 'templates') {
    if (-not (Test-Path (Join-Path $pub $need))) { throw "publish output has no $need" }
}

$stageRoot = Join-Path $root "build\windows-$arch"
if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }
$stage = Join-Path $stageRoot 'Portly'
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Recurse -Force (Join-Path $pub '*') $stage
Copy-Item -Force (Join-Path $root 'LICENSE') $stage -ErrorAction SilentlyContinue

$name = "Portly-$label-windows-$arch.zip"
$zip = Join-Path $dist $name
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# includeBaseDirectory = true: entries start with "Portly/" (forward slashes on .NET Framework 4.6.1+ and .NET)
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Copy-Item -Force $zip (Join-Path $dist "Portly-windows-$arch.zip")

# SHA256SUMS for every Portly-* file in dist (sha256sum format: two spaces), the file tools/install.sh and users verify against
$utf8 = New-Object System.Text.UTF8Encoding($false)
$sums = Get-ChildItem $dist -File -Filter 'Portly-*' | Sort-Object Name | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS'), (($sums -join "`n") + "`n"), $utf8)
Get-ChildItem $dist -File | Format-Table Name, Length -AutoSize
