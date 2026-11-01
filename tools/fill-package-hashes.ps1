<#
  Adds "sha256" to every package in manifest\packages.json and manifest\packages-legacy.json that has none, by
  downloading it. Many GB: meant for the fill-package-hashes workflow, not for a local run. Files are hashed while they
  stream and are not stored.

  Resumable: each hash is written to the manifest right away, so a new run skips what is done. Hashes are also kept in
  -State; -StateOnly applies those to a fresh checkout without downloading anything (used when main moved meanwhile).
  A package that fails stays without a hash and is listed at the end; the run still succeeds, try again later.

  Moving URLs (latest, gh-pages ...) are skipped on purpose: the file behind them changes, a pinned hash would refuse
  the next good version. They keep installing over HTTPS with a warning in the log.

  Sign afterwards: a changed manifest has no valid signature (tools\sign-manifests.ps1).
#>
param(
    [string[]]$Manifests = @('manifest\packages.json', 'manifest\packages-legacy.json'),
    [string]$State = 'build\package-hashes.json',
    [int]$MaxMinutes = 270,
    [switch]$StateOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$State = Join-Path $root $State
$utf8 = New-Object System.Text.UTF8Encoding($false)
$started = Get-Date

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromHours(1)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Mozilla/5.0 (Windows NT 10.0; Win64; x64) Portly/fill-package-hashes (+https://github.com/erdemsoftware/portly)')

# the same fallbacks as Installer.CandidateUrls
function Get-CandidateUrls([string]$url) {
    $url
    if ($url -like '*windows.php.net/downloads/releases/*' -and $url -notlike '*/archives/*') { $url.Replace('/downloads/releases/', '/downloads/releases/archives/') }
    if ($url -like 'https://dlcdn.apache.org/*') { 'https://archive.apache.org/dist/' + $url.Substring('https://dlcdn.apache.org/'.Length) }
}

function Get-RemoteHash([string]$url) {
    $response = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        if (-not $response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode)" }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return (-join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') })) }
        finally { $sha.Dispose(); $stream.Dispose() }
    }
    finally { $response.Dispose() }
}

function Save-Text([string]$file, [string]$text) {
    [IO.File]::WriteAllText("$file.tmp", $text, $utf8)
    Move-Item -Force "$file.tmp" $file
}

function Save-State { Save-Text $State ($known | ConvertTo-Json) }

# url -> hash: what earlier runs found, plus every hash already in the manifests
$known = @{}
if (Test-Path $State) { (Get-Content $State -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $known[$_.Name] = $_.Value } }
else { New-Item -ItemType Directory -Force (Split-Path $State) | Out-Null }

# a package is one flat { ... } with a "folder"; the "vcredist" object has a url too, but no folder
$entry = New-Object regex '\{[^{}]*"folder"[^{}]*\}'
$paths = $Manifests | ForEach-Object { Join-Path $root $_ }
foreach ($path in $paths) {
    foreach ($m in $entry.Matches([IO.File]::ReadAllText($path))) {
        $u = [regex]::Match($m.Value, '"url":\s*"([^"]+)"').Groups[1].Value
        $h = [regex]::Match($m.Value, '"sha256":\s*"([0-9a-fA-F]{64})"').Groups[1].Value
        if ($h) { $known[$u] = $h.ToLowerInvariant() }
    }
}

$filled = 0; $moving = @(); $failed = @(); $outOfTime = $false
foreach ($path in $paths) {
    $text = [IO.File]::ReadAllText($path)
    $todo = @($entry.Matches($text) | Where-Object { $_.Value -notmatch '"sha256"' } | ForEach-Object { $_.Value })
    foreach ($block in $todo) {
        $line = [regex]::Match($block, '(?m)^([ \t]*)"url":\s*"([^"]+)",(\r?\n)')
        if (-not $line.Success) { $failed += "no url line to extend in: $block"; continue }
        $url = $line.Groups[2].Value
        $version = [regex]::Match($block, '"version":\s*"([^"]*)"').Groups[1].Value
        if ($url -match '(?i)latest|gh-pages|nightly|snapshot' -or $version -match '(?i)latest') { $moving += $url; continue }

        if (-not $known.ContainsKey($url)) {
            if ($StateOnly) { continue }
            if (((Get-Date) - $started).TotalMinutes -ge $MaxMinutes) { $outOfTime = $true; break }
            Write-Host "Downloading $url"
            $errorText = $null
            foreach ($candidate in Get-CandidateUrls $url) {
                try { $known[$url] = Get-RemoteHash $candidate; $errorText = $null; break }
                catch { $errorText = $_.Exception.Message; Write-Host "  $candidate -> $errorText" -ForegroundColor Yellow }
            }
            if ($errorText) { $failed += "$url ($errorText)"; continue }
            Save-State
        }

        $sha = '{0}"sha256": "{1}",{2}' -f $line.Groups[1].Value, $known[$url], $line.Groups[3].Value
        $after = $line.Index + $line.Length
        $text = $text.Replace($block, $block.Insert($after, $sha))
        $null = $text | ConvertFrom-Json
        Save-Text $path $text   # right away: a stopped run keeps everything found so far
        $filled++
    }
    if ($outOfTime) { break }
}
$client.Dispose()

$summary = @("Filled $filled package(s).")
if ($outOfTime) { $summary += "Stopped after $MaxMinutes minutes: run it again to continue." }
if ($moving.Count) { $summary += "Moving URLs, left without a hash on purpose:"; $summary += ($moving | Sort-Object -Unique | ForEach-Object { "- $_" }) }
if ($failed.Count) { $summary += "Failed, still without a hash:"; $summary += ($failed | ForEach-Object { "- $_" }) }
$summary | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
