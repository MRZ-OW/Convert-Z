# ConvertZ installer (current user, no admin)
#   install.ps1                  install or update
#   install.ps1 -Remove          uninstall
#   install.ps1 -SelfTest        also run the self-test
#   install.ps1 -RestartExplorer restart Explorer without asking
param([switch]$Remove, [switch]$RestartExplorer, [switch]$SelfTest)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "ConvertZ.exe"
if (-not (Test-Path $src)) { $src = Join-Path $PSScriptRoot "dist\ConvertZ.exe" }
$dst = Join-Path $env:LOCALAPPDATA "Programs\ConvertZ"
$exe = Join-Path $dst "ConvertZ.exe"

function Wait-Idle {
    $running = Get-Process ConvertZ -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Waiting for the current conversion to finish..." -ForegroundColor Yellow
        $running | Wait-Process
    }
}

if ($Remove) {
    Wait-Idle
    if (Test-Path $exe) { Start-Process $exe -ArgumentList "--unregister" -Wait }
    elseif (Test-Path $src) { Start-Process $src -ArgumentList "--unregister" -Wait }
    foreach ($d in $dst, (Join-Path $env:LOCALAPPDATA "ConvertZ")) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    Write-Host "ConvertZ removed." -ForegroundColor Green
    return
}

if (-not (Test-Path $src)) { throw "ConvertZ.exe not found next to install.ps1" }

function Find-Ffmpeg {
    $dirs = @()
    foreach ($scope in "Process", "User", "Machine") {
        foreach ($d in ([Environment]::GetEnvironmentVariable("PATH", $scope) -split ";")) {
            if ($d) { $dirs += [Environment]::ExpandEnvironmentVariables($d.Trim('"')) }
        }
    }
    $dirs += Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links"
    $pk = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    if (Test-Path $pk) {
        $dirs += Get-ChildItem $pk -Directory -Filter "*FFmpeg*" |
            ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue } |
            Sort-Object LastWriteTime -Descending | ForEach-Object { $_.DirectoryName }
    }
    foreach ($d in $dirs) {
        if ((Test-Path (Join-Path $d "ffmpeg.exe")) -and (Test-Path (Join-Path $d "ffprobe.exe"))) { return Join-Path $d "ffmpeg.exe" }
    }
    return $null
}

$ff = Find-Ffmpeg
if (-not $ff) {
    Write-Host "ffmpeg not found, installing it with winget..." -ForegroundColor Cyan
    & winget install -e --id Gyan.FFmpeg
    $ff = Find-Ffmpeg
    if (-not $ff) { throw "Still no ffmpeg. Install it (winget install Gyan.FFmpeg) and run this again." }
}
Write-Host "ffmpeg: $ff"

Wait-Idle
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item $src $exe -Force
Unblock-File $exe
Write-Host "Installed to $dst"

$p = Start-Process $exe -ArgumentList "--register" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Couldn't add the menu entries." }
$n = (Get-ChildItem "HKCU:\Software\Classes\SystemFileAssociations" | Where-Object { Test-Path (Join-Path $_.PSPath "shell\ConvertZ") }).Count
Write-Host "Added 'Convert to' to $n file types." -ForegroundColor Green

# Windows 11 hides new tray icons behind the ^ arrow. Show it once so it gets an entry, then pin it.
Write-Host "Showing the tray icon once..."
Start-Process $exe -ArgumentList "--prime" -Wait
$nis = "HKCU:\Control Panel\NotifyIconSettings"
if (Test-Path $nis) {
    $entry = Get-ChildItem $nis | Where-Object { (Get-ItemProperty $_.PSPath).ExecutablePath -eq $exe } | Select-Object -First 1
    if ($entry -and (Get-ItemProperty $entry.PSPath).IsPromoted -eq 1) {
        # Explorer only reads this when it starts
        $restart = $RestartExplorer
        if (-not $restart -and $Host.Name -eq "ConsoleHost") {
            $a = Read-Host "Restart Explorer so the progress icon shows on the taskbar now? Open folders will close. (y/N)"
            $restart = $a -match '^y'
        }
        if ($restart) {
            Stop-Process -Name explorer -Force
            Start-Sleep -Seconds 3
            if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
        } else {
            Write-Host "The tray icon will show on the taskbar after you next sign in. To show it now: Settings > Personalization > Taskbar > Other system tray icons > ConvertZ."
        }
    } else {
        Write-Host "To keep the progress icon visible: Settings > Personalization > Taskbar > Other system tray icons > ConvertZ." -ForegroundColor Yellow
    }
}

Write-Host "`nChecking GPU encoding..." -ForegroundColor Cyan
$report = Join-Path $env:TEMP "ConvertZ-gpucheck.txt"
$p = Start-Process $exe -ArgumentList "--gpucheck", "`"$report`"" -Wait -PassThru
Get-Content $report -Encoding UTF8 | Out-Host
switch ($p.ExitCode) {
    0 { Write-Host "Video is converted on the GPU." -ForegroundColor Green }
    4 { Write-Host "No NVIDIA GPU, video is converted on the CPU." -ForegroundColor Yellow }
    default { Write-Host "Some GPU paths failed (see above). Those conversions use the CPU instead." -ForegroundColor Yellow }
}

if ($SelfTest) {
    Write-Host "`nRunning the self-test, this takes a minute or two..." -ForegroundColor Cyan
    $st = Join-Path $env:TEMP "ConvertZ-selftest.txt"
    $p = Start-Process $exe -ArgumentList "--selftest", "`"$st`"" -Wait -PassThru
    Get-Content $st -Encoding UTF8 | Where-Object { $_ -notmatch "^\s+ok\s" } | Out-Host
    Write-Host "Full report: $st"
    if ($p.ExitCode -ne 0) { Write-Host "Self-test FAILED" -ForegroundColor Red } else { Write-Host "Self-test passed" -ForegroundColor Green }
}

Write-Host "`nDone. Right-click a video, audio or image file and pick Convert to." -ForegroundColor Green
