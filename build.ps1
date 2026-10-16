# .\build.ps1 [-SelfTest] [-Snapshot]
# Output: dist\ and ConvertZ.zip (exe + installer + uninstaller)
param([switch]$SelfTest, [switch]$Snapshot)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\make-icon.ps1"
if ($LASTEXITCODE -ne 0) { throw "icon failed" }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& dotnet build "$root\src\ConvertZ.csproj" -c Release -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Get-ChildItem $dist -Exclude ConvertZ.exe | Remove-Item -Recurse -Force
$release = "install.ps1", "Install.cmd", "Uninstall.cmd"
foreach ($f in $release) { Copy-Item (Join-Path $root $f) $dist }

$exe = Join-Path $dist "ConvertZ.exe"
if ($SelfTest) {
    $log = Join-Path $root "selftest.log"
    $p = Start-Process $exe -ArgumentList "--selftest", "`"$log`"" -Wait -PassThru
    Get-Content $log -Encoding UTF8 | Out-Host
    if ($p.ExitCode -ne 0) { throw "self-test failed" }
}
if ($Snapshot) { Start-Process $exe -ArgumentList "--snapshot", "`"$root\snapshots`"" -Wait }

$zip = Join-Path $root "ConvertZ.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (@("ConvertZ.exe") + $release | ForEach-Object { Join-Path $dist $_ }) -DestinationPath $zip
Write-Host "built $exe and $zip" -ForegroundColor Green
