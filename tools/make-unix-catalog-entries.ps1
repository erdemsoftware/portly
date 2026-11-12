<#
  Turns the artifacts of the "v2 own builds" workflow (tools/own-builds, .github/workflows/v2-builds.yml) into catalog entries
  for manifest/packages-linux.json and manifest/packages-macos.json. Nothing is edited: two snippet files are written, to be pasted
  into the "packages" array of each catalog (then the catalogs are signed as usual).

  Usage
    gh run download <run id> -n own-builds-all -D own-builds
    pwsh tools/make-unix-catalog-entries.ps1 -ArtifactsDir own-builds [-BaseUrl https://github.com/erdemsoftware/portly-builds/releases/download] [-OutDir own-builds]

  Every package <name>-<version>-<os>-<arch>.tar.gz is expected as a release asset of the tag <name>-<version>:
    <BaseUrl>/<name>-<version>/<name>-<version>-<os>-<arch>.tar.gz
  (so one release per program version holds its 4 files; SHA256SUMS can be uploaded to each too).
  Hashes come from SHA256SUMS in the folder, or are computed from the files when it is missing.
#>
param(
    [Parameter(Mandatory = $true)][string]$ArtifactsDir,
    [string]$BaseUrl = 'https://github.com/erdemsoftware/portly-builds/releases/download',
    [string]$OutDir = '',
    # which PHP line gets "recommended": the newest one found by default
    [string]$RecommendedPhpLine = ''
)

$ErrorActionPreference = 'Stop'
if ($OutDir -eq '') { $OutDir = $ArtifactsDir }
$BaseUrl = $BaseUrl.TrimEnd('/')

$sums = @{}
$sumFile = Join-Path $ArtifactsDir 'SHA256SUMS'
if (Test-Path $sumFile) {
    foreach ($line in Get-Content $sumFile) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $sums[$Matches[2].Trim()] = $Matches[1].ToLower() }
    }
}

# catalog type of each program, how the folder is named, the note shown in the packages window
$types = @{
    nginx     = @{ type = 'nginx';     folder = '{0}-{1}'; note = 'built by Portly (OpenSSL, HTTP/2 and HTTP/3, no system libraries needed)' }
    httpd     = @{ type = 'apache';    folder = '{0}-{1}'; note = 'built by Portly (mod_ssl, mod_http2, mod_proxy_fcgi, mod_rewrite)' }
    memcached = @{ type = 'memcached'; folder = '{0}-{1}'; note = 'built by Portly' }
    php       = @{ type = 'php';       folder = '{0}-{1}-xdebug'; note = 'php-fpm + php-cli with Xdebug (ext/xdebug.so), built by Portly' }
}

$files = Get-ChildItem -Path $ArtifactsDir -Filter '*.tar.gz' -Recurse
$entries = @()
foreach ($f in $files) {
    if ($f.Name -notmatch '^(nginx|httpd|memcached|php)-(\d+(?:\.\d+)+)-(linux|macos)-(x64|arm64)\.tar\.gz$') { Write-Warning ('skipped ' + $f.Name); continue }
    $name = $Matches[1]; $version = $Matches[2]; $os = $Matches[3]; $arch = $Matches[4]
    $hash = $sums[$f.Name]
    if (-not $hash) { $hash = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash.ToLower() }
    $t = $types[$name]
    $entries += [pscustomobject]@{
        Name = $name; Type = $t.type; Version = $version; Os = $os; Arch = $arch
        Folder = ($t.folder -f $name, $version)
        Url = ('{0}/{1}-{2}/{3}' -f $BaseUrl, $name, $version, $f.Name)
        Sha256 = $hash; Note = $t.note; Size = $f.Length
        Line = if ($name -eq 'php') { ($version -split '\.')[0..1] -join '.' } else { '' }
    }
}
if ($entries.Count -eq 0) { throw "no <name>-<version>-<os>-<arch>.tar.gz found in $ArtifactsDir" }

if ($RecommendedPhpLine -eq '') {
    $RecommendedPhpLine = ($entries | Where-Object { $_.Name -eq 'php' } | ForEach-Object { [version]$_.Line } | Sort-Object -Descending | Select-Object -First 1)
    if ($RecommendedPhpLine) { $RecommendedPhpLine = $RecommendedPhpLine.ToString() }
}

function Q([string]$s) { return '"' + $s.Replace('\', '\\').Replace('"', '\"') + '"' }

# the other programs: the highest version of each (type, os, arch) is "recommended"
$best = @{}
foreach ($e in $entries) {
    $k = '{0}|{1}|{2}' -f $e.Type, $e.Os, $e.Arch
    if ($e.Name -eq 'php') { continue }
    if (-not $best.ContainsKey($k) -or [version]$e.Version -gt [version]$best[$k]) { $best[$k] = $e.Version }
}

foreach ($os in 'linux', 'macos') {
    $json = New-Object System.Collections.Generic.List[string]
    $mine = @($entries | Where-Object { $_.Os -eq $os } |
        Sort-Object @{ Expression = { @('nginx', 'httpd', 'memcached', 'php').IndexOf($_.Name) } }, @{ Expression = { [version]$_.Version }; Descending = $true }, Arch)
    foreach ($e in $mine) {
        $k = '{0}|{1}|{2}' -f $e.Type, $e.Os, $e.Arch
        $rec = if ($e.Name -eq 'php') { $e.Line -eq $RecommendedPhpLine } else { $best[$k] -eq $e.Version }
        $lines = @(
            ('    "type": ' + (Q $e.Type)),
            ('    "version": ' + (Q $e.Version)),
            ('    "folder": ' + (Q $e.Folder)),
            ('    "url": ' + (Q $e.Url)),
            ('    "sha256": ' + (Q $e.Sha256)),
            '    "kind": "tar.gz"',
            ('    "arch": ' + (Q $e.Arch)),
            ('    "note": ' + (Q $e.Note))
        )
        if ($rec) { $lines += '    "recommended": true' }
        $json.Add("  {`n" + ($lines -join ",`n") + "`n  }")
    }
    $out = Join-Path (Resolve-Path $OutDir) ("catalog-$os.snippet.json")
    $text = "[`n" + ($json -join ",`n") + "`n]`n"
    [System.IO.File]::WriteAllText($out, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ('{0}: {1} entries -> {2}' -f $os, $mine.Count, $out)
}
Write-Host 'Upload each file to the release tagged <name>-<version> of the builds repository, then paste the snippets into the catalogs.'
