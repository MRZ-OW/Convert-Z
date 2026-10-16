# Clicks the installed Convert to menu through the shell (1, 3 and 20 files) and checks the results. Run install.ps1 first.
$ErrorActionPreference = "Stop"
$exe = Join-Path $env:LOCALAPPDATA "Programs\ConvertZ\ConvertZ.exe"
if (-not (Test-Path $exe)) { throw "install first: .\install.ps1" }
$ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
$work = Join-Path $env:TEMP "ConvertZ-e2e"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null
$log = Join-Path $env:LOCALAPPDATA "ConvertZ\ConvertZ.log"
$fail = 0
function Ok($cond, $what) { if ($cond) { Write-Host "  ok   $what" -ForegroundColor Green } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:fail++ } }

function Gen([string[]]$a) { & $ff -hide_banner -nostdin -y -v error @a; if ($LASTEXITCODE -ne 0) { throw "ffmpeg fixture failed" } }
1..3 | ForEach-Object { Gen @("-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=6", "-f", "lavfi", "-i", "sine=f=440:d=6", "-f", "lavfi", "-i", "sine=f=660:d=6",
        "-map", "0", "-map", "1", "-map", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", (Join-Path $work "2026-10-15 20-47-1$_.mkv")) }
$wavs = 1..20 | ForEach-Object { $p = Join-Path $work ("voice memo {0:00}.wav" -f $_); Gen @("-f", "lavfi", "-i", "sine=f=$(200 + $_ * 20):d=2", $p); $p }
Gen @("-f", "lavfi", "-i", "testsrc2=s=400x300", "-frames:v", "1", "-update", "1", (Join-Path $work "screenshot.png"))

function Wait-Quiet([int]$timeoutSec = 120) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds 400
    while ((Get-Process ConvertZ -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalSeconds -lt $timeoutSec) { Start-Sleep -Milliseconds 250 }
    return -not (Get-Process ConvertZ -ErrorAction SilentlyContinue)
}

function Shell([string]$name, [string[]]$files, [string]$invoke) {
    $report = Join-Path $work "$name.report.txt"
    $a = @("--shelltest", "`"$report`"") + ($files | ForEach-Object { "`"$_`"" })
    if ($invoke) { $a += @("--invoke", $invoke) }
    $p = Start-Process $exe -ArgumentList $a -Wait -PassThru
    $text = Get-Content $report -Encoding UTF8
    return @{ Code = $p.ExitCode; Text = $text }
}

$before = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object).Count

Write-Host "1) one MKV -> MP4 (right-click, Convert to, MP4)" -ForegroundColor Cyan
$mkv = Join-Path $work "2026-10-15 20-47-11.mkv"
$r = Shell "single" @($mkv) "mp4"
$r.Text | Where-Object { $_ -match "^(Convert to|    |RESULT|INVOKE)" } | ForEach-Object { "     $_" }
Ok ($r.Code -eq 0) "menu shows the expected entries and the click went through"
Ok (Wait-Quiet) "ConvertZ exited by itself"
Ok (Test-Path (Join-Path $work "2026-10-15 20-47-11.mp4")) "2026-10-15 20-47-11.mp4 created next to the source"
$streams = & ffprobe -v error -show_entries stream=codec_type -of csv=p=0 (Join-Path $work "2026-10-15 20-47-11.mp4")
Ok (($streams | Where-Object { $_ -eq "audio" }).Count -eq 2) "both OBS audio tracks kept"

Write-Host "2) three MKVs selected -> MP4" -ForegroundColor Cyan
$three = 1..3 | ForEach-Object { Join-Path $work "2026-10-15 20-47-1$_.mkv" }
$r = Shell "three" $three "mp4"
Ok ($r.Code -eq 0) "menu with 3 selected"
Ok (Wait-Quiet) "ConvertZ exited by itself"
Ok ((Test-Path (Join-Path $work "2026-10-15 20-47-11 (1).mp4")) -and (Test-Path (Join-Path $work "2026-10-15 20-47-12.mp4")) -and (Test-Path (Join-Path $work "2026-10-15 20-47-13.mp4"))) "three outputs (the existing one got ' (1)')"

Write-Host "3) twenty WAVs selected -> MP3 (more than Explorer's 15-item limit for normal verbs)" -ForegroundColor Cyan
$r = Shell "twenty" $wavs "mp3"
Ok ($r.Code -eq 0) "menu still there with 20 selected"
Ok (Wait-Quiet) "ConvertZ exited by itself"
$mp3 = (Get-ChildItem $work -Filter "voice memo *.mp3").Count
Ok ($mp3 -eq 20) "20 MP3s created ($mp3)"

Write-Host "4) image" -ForegroundColor Cyan
$r = Shell "image" @((Join-Path $work "screenshot.png")) "jpg"
Ok ($r.Code -eq 0) "png menu"
Ok (Wait-Quiet) "ConvertZ exited by itself"
Ok (Test-Path (Join-Path $work "screenshot.jpg")) "screenshot.jpg"

Write-Host "5) leftovers" -ForegroundColor Cyan
$new = Get-Content $log -Encoding UTF8 | Select-Object -Skip $before
$owners = ($new | Select-String "owner start").Count
$handoffs = ($new | Select-String "\] queued ").Count
Write-Host "     owners: $owners, handed-off launches: $handoffs"
Ok ($owners -le 6) "launches were merged into few tray owners (not one per file)"
Ok (-not (Get-ChildItem $work -Filter "*.converting.*")) "no temp files"
Ok (-not (Get-Process ConvertZ -ErrorAction SilentlyContinue)) "no ConvertZ process"
$orphans = Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" | Where-Object { $_.CommandLine -like "*ConvertZ-e2e*" }
Ok (-not $orphans) "no ffmpeg left from these jobs"
Ok (-not ($new | Select-String -CaseSensitive "\] FAILED |CRASH")) "log has no failed jobs"

if ($fail) { Write-Host "$fail FAILED" -ForegroundColor Red; exit 1 }
Write-Host "ALL PASSED" -ForegroundColor Green
