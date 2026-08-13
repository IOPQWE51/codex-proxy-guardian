<#
.SYNOPSIS
    生成 Codex 代理守护图标（dist\guardian.ico）
.DESCRIPTION
    用 GDI+ 绘制绿色圆形徽章 + 白色 P 字，输出 16/32/48 多尺寸 ICO
    （经典 BMP 格式，兼容 csc /win32icon 嵌入）。
    用法:
        .\build-icons.ps1
#>
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'guardian.ico'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 32, 48)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $body = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(34, 160, 95))
    $g.FillEllipse($body, 0, 0, $s - 1, $s - 1)
    $body.Dispose()

    $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 255, 255, 255), [Math]::Max(1, [Math]::Round($s * 0.06)))
    $inset = [Math]::Round($s * 0.12)
    $g.DrawEllipse($ringPen, $inset, $inset, $s - 1 - 2 * $inset, $s - 1 - 2 * $inset)
    $ringPen.Dispose()

    $px = [Math]::Round($s * 0.62)
    $font = New-Object System.Drawing.Font('Segoe UI', $px, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $tf = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $sizeF = $g.MeasureString('P', $font)
    $g.DrawString('P', $font, $tf, ($s - $sizeF.Width) / 2, ($s - $sizeF.Height) / 2 - [Math]::Round($s * 0.03))
    $tf.Dispose()
    $font.Dispose()
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $data = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    $script:pngs += , $data
}

$n = $sizes.Count
$header = 6 + 16 * $n
$offset = $header
$total = $header
foreach ($d in $pngs) { $total += $d.Length }

$bytes = New-Object byte[] $total
$br = New-Object System.IO.BinaryWriter([System.IO.MemoryStream]::new($bytes))
# ICONDIR
$br.Write([UInt16]0); $br.Write([UInt16]1); $br.Write([UInt16]$n)
$idx = 0
$cur = $header
foreach ($d in $pngs) {
    $s = $sizes[$idx]
    $br.Write([Byte]($(if ($s -ge 256) {0} else {$s})))
    $br.Write([Byte]($(if ($s -ge 256) {0} else {$s})))
    $br.Write([Byte]0); $br.Write([Byte]0)
    $br.Write([UInt16]1); $br.Write([UInt16]32)
    $br.Write([UInt32]$d.Length)
    $br.Write([UInt32]$cur)
    $cur += $d.Length
    $idx++
}
foreach ($d in $pngs) { $br.Write($d) }
$br.Flush()
[IO.File]::WriteAllBytes($out, $bytes)
Write-Output "图标已生成: $out ($($bytes.Length) bytes)"
Get-Item -LiteralPath $out | Select-Object FullName, Length