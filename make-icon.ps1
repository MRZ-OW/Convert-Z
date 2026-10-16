# Draws src\Resources\app.ico from IconArt.cs
param([string]$Out = "$PSScriptRoot\src\Resources\app.ico")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -Path "$PSScriptRoot\src\IconArt.cs" -ReferencedAssemblies System.Drawing
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = [ConvertZ.IconArt]::App($s)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs[$s] = $ms.ToArray()
}

# ico header, directory, png images
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$s].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $fs.Close()
Write-Host "wrote $Out ($((Get-Item $Out).Length) bytes)"
