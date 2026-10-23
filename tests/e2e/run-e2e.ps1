# End-to-end test of a real Portly build in its own folder (build\e2e) on ports 19080 / 19443 / 19306:
# 8 sites with PHP 7.4 / 8.2 / 8.3 / 8.4 / 8.5 over Apache, Nginx and Caddy, strict HTTPS checks against Portly's CA,
# headless Chrome, aliases, Fix SSL, phpMyAdmin / Adminer, 80 parallel requests. Packages are copied from
# build\smoketest\bin (run the smoke test once first); missing PHP versions are downloaded.
param([string]$MysqlDir = "C:\laragon\bin\mysql\mysql-8.4.11-winx64")
$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot\..\.."
$r = "$repo\build\e2e"
$src = "$repo\build\smoketest\bin"
if (Test-Path $r) { Get-ChildItem "$r\bin\mysql" -ErrorAction SilentlyContinue | ForEach-Object { cmd /c rmdir "$($_.FullName)" }; Remove-Item $r -Recurse -Force }
New-Item -ItemType Directory -Force "$r\usr", "$r\bin\php", "$r\bin\mysql" | Out-Null
Copy-Item "$repo\build\Modern\*" $r -Recurse -Exclude usr, www, data, logs, tmp, bin, etc
foreach ($t in 'apache', 'nginx', 'caddy', 'mailpit', 'phpmyadmin', 'adminer', 'composer') { Copy-Item "$src\$t" "$r\bin\$t" -Recurse }
Get-ChildItem "$src\php" -Directory | ForEach-Object { Copy-Item $_.FullName "$r\bin\php" -Recurse }
cmd /c mklink /J "$r\bin\mysql\$(Split-Path $MysqlDir -Leaf)" "$MysqlDir" | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /platform:x64 /out:"$r\E2E.exe" /r:"$r\Portly.exe" /r:System.Net.dll "$PSScriptRoot\E2E.cs"
Push-Location $r
try { .\E2E.exe "$r\shots"; exit $LASTEXITCODE } finally { Pop-Location }
