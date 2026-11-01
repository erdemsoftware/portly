<#
  Builds the smoke test and runs the groups that need no big downloads, no fixed ports and no services.
  Used by the build and release workflows; a failed build or a failed group ends with exit code 1.
    pwsh tools\run-security-tests.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$groups = '--share-security', '--updates-security'

$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'), "$env:ProgramFiles\dotnet\dotnet.exe") | Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) } | Select-Object -First 1
if (-not $dotnet) { throw ".NET SDK not found" }

& $dotnet build (Join-Path $root 'tests\Portly.SmokeTest\Portly.SmokeTest.csproj') -c Release --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { Write-Host "Smoke test build failed"; exit 1 }

$exe = Join-Path $root 'build\smoketest\Portly.SmokeTest.exe'
$failed = @()
foreach ($g in $groups) {
    Write-Host "== $g" -ForegroundColor Cyan
    & $exe $g
    if ($LASTEXITCODE -ne 0) { $failed += "$g (exit code $LASTEXITCODE)" }
}
if ($failed.Count) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "All groups passed" -ForegroundColor Green
