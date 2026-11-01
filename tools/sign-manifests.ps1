<#
  Signs manifest\*.json: writes <name>.json.sig next to each file (base64, RSA PKCS#1 v1.5 + SHA-256, over the exact bytes).
  Portly refuses a remote manifest without a valid signature (src\Portly\Packages\SignedManifest.cs).

  The private key comes from the environment and is never printed:
    PORTLY_MANIFEST_KEY     required, PEM (PowerShell 7) or .NET XML, at least 3072 bits
    PORTLY_MANIFEST_KEY_2   optional second key during a rotation; its signature is added as a second line

  Usage:
    pwsh tools\sign-manifests.ps1                    # sign (the same bytes give the same .sig: no diff, no commit)
    pwsh tools\sign-manifests.ps1 -CheckKey          # only check the key: set, private, strong, listed in ManifestKeys.cs
    pwsh tools\sign-manifests.ps1 -VerifyCommitted   # no secret needed: HEAD's manifests against ManifestKeys.cs
  -Dir signs another folder and -NoKeyCheck skips the ManifestKeys.cs check; both exist for the smoke test.
#>
param(
    [string]$Dir = (Join-Path $PSScriptRoot '..\manifest'),
    [switch]$CheckKey,
    [switch]$VerifyCommitted,
    [switch]$NoKeyCheck
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$keysFile = Join-Path $root 'src\Portly\Packages\ManifestKeys.cs'
$hash = [System.Security.Cryptography.HashAlgorithmName]::SHA256
$padding = [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
$minBits = 3072

# the public keys Portly trusts, as compiled into the app
function Get-TrustedKeys {
    $source = [IO.File]::ReadAllText($keysFile)
    $keys = @([regex]::Matches($source, '"(<RSAKeyValue>.*?</RSAKeyValue>)"') | ForEach-Object { $_.Groups[1].Value })
    if ($keys.Count -eq 0) { throw "ManifestKeys.cs still holds the placeholder: paste the real public key first (docs\03-yayinlama.md)" }
    foreach ($k in $keys) { if ($k -match '<D>') { throw "ManifestKeys.cs holds a PRIVATE key: remove it from the source and replace the key" } }
    return $keys
}

function Get-Modulus([string]$xml) {
    [Convert]::ToBase64String([Convert]::FromBase64String([regex]::Match($xml, '<Modulus>(.*?)</Modulus>').Groups[1].Value))
}

# error messages name the variable only: nothing of its value may reach the log
function Read-SigningKey([string]$name) {
    $text = [Environment]::GetEnvironmentVariable($name)
    $rsa = [System.Security.Cryptography.RSA]::Create()
    if ($text -like '*<RSAKeyValue>*') {
        try { $rsa.FromXmlString($text) } catch { throw "$name is not a valid XML key" }
    }
    elseif ($text -like '*-----BEGIN*') {
        if ($PSVersionTable.PSVersion.Major -lt 7) { throw "$name is PEM: run this with PowerShell 7, or use the XML form" }
        try { $rsa.ImportFromPem($text) } catch { throw "$name is not a valid PEM key" }
    }
    else { throw "$name is neither PEM nor XML" }
    if ($rsa.KeySize -lt $minBits) { throw "$name is too short: $($rsa.KeySize) bits, at least $minBits needed" }
    try { $null = $rsa.ExportParameters($true) } catch { throw "$name holds no private key" }
    return $rsa
}

function Test-Signature([byte[]]$data, [string]$signature, [string[]]$keys) {
    foreach ($line in ($signature -split "`r?`n" | Where-Object { $_.Trim() -ne '' })) {
        try { $sig = [Convert]::FromBase64String($line.Trim()) } catch { continue }
        foreach ($xml in $keys) {
            $rsa = [System.Security.Cryptography.RSA]::Create()
            $rsa.FromXmlString($xml)
            if ($rsa.VerifyData($data, $sig, $hash, $padding)) { return $true }
        }
    }
    return $false
}

# raw blob bytes: what raw.githubusercontent.com serves, whatever the working copy holds
function Get-Blob([string]$spec) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'git', "cat-file blob $spec"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.WorkingDirectory = $root
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = New-Object System.IO.MemoryStream
    $p.StandardOutput.BaseStream.CopyTo($out)
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "git cat-file failed: $spec" }
    return , $out.ToArray()
}

if ($VerifyCommitted) {
    $trusted = Get-TrustedKeys
    Push-Location $root
    try { $names = @(git ls-tree --name-only HEAD manifest/ | Where-Object { $_ -like '*.json' }) } finally { Pop-Location }
    if ($names.Count -eq 0) { throw "No manifests in HEAD" }
    foreach ($n in $names) {
        $sig = [Text.Encoding]::ASCII.GetString((Get-Blob "HEAD:$n.sig"))
        if (-not (Test-Signature (Get-Blob "HEAD:$n") $sig $trusted)) { throw "Bad signature in HEAD: $n" }
        Write-Host "  verified $n" -ForegroundColor Green
    }
    return
}

if (-not $env:PORTLY_MANIFEST_KEY) { throw "PORTLY_MANIFEST_KEY is not set: manifests must not ship unsigned" }
$signers = @(Read-SigningKey 'PORTLY_MANIFEST_KEY')
if ($env:PORTLY_MANIFEST_KEY_2) { $signers += Read-SigningKey 'PORTLY_MANIFEST_KEY_2' }

$verifyKeys = @($signers | ForEach-Object { $_.ToXmlString($false) })
if (-not $NoKeyCheck) {
    $trusted = @(Get-TrustedKeys | ForEach-Object { Get-Modulus $_ })
    foreach ($k in $verifyKeys) {
        if ($trusted -notcontains (Get-Modulus $k)) { throw "A signing key does not match any public key in ManifestKeys.cs: clients would refuse every manifest" }
    }
}
if ($CheckKey) { Write-Host "Signing key is usable ($($signers.Count) key(s))" -ForegroundColor Green; return }

$files = @(Get-ChildItem $Dir -Filter *.json)
if ($files.Count -eq 0) { throw "No manifests in $Dir" }
foreach ($f in $files) {
    $data = [IO.File]::ReadAllBytes($f.FullName)
    $lines = @($signers | ForEach-Object { [Convert]::ToBase64String($_.SignData($data, $hash, $padding)) })
    $text = ($lines -join "`n") + "`n"
    if (-not (Test-Signature $data $text $verifyKeys)) { throw "Signature check failed: $($f.Name)" }
    [IO.File]::WriteAllBytes($f.FullName + '.sig', [Text.Encoding]::ASCII.GetBytes($text))
    Write-Host "  signed $($f.Name)" -ForegroundColor Green
}
