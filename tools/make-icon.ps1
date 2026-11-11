<#
  MyTube のアイコンを描いて .ico / .png に書き出す。

  使い方:
    pwsh tools/make-icon.ps1                  # 既定デザインで MyTube/Assets に出力
    pwsh tools/make-icon.ps1 -Style monogram  # デザインを変える
    pwsh tools/make-icon.ps1 -Preview out.png # 全デザインの見比べ画像を作る
#>
param(
    [ValidateSet('screen', 'bookmark', 'monogram')]
    [string]$Style = 'screen',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\MyTube\Assets'),
    [string]$Preview
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# 角の丸い多角形を塗る（同じ色の太い線で縁取って角を丸める）
function Fill-RoundPolygon($g, [System.Drawing.Color]$color, [float]$round, [float[]]$xy) {
    [System.Drawing.PointF[]]$pts = for ($i = 0; $i -lt $xy.Length; $i += 2) { [System.Drawing.PointF]::new($xy[$i], $xy[$i + 1]) }
    $brush = [System.Drawing.SolidBrush]::new($color)
    $pen = [System.Drawing.Pen]::new($color, $round)
    $pen.LineJoin = 'Round'
    $g.FillPolygon($brush, $pts)
    $g.DrawPolygon($pen, $pts)
}

function Fill-Heart($g, [System.Drawing.Color]$color, [float]$cx, [float]$cy, [float]$s) {
    $brush = [System.Drawing.SolidBrush]::new($color)
    $r = 0.27 * $s
    $g.FillEllipse($brush, $cx - 0.23 * $s - $r, $cy - 0.12 * $s - $r, 2 * $r, 2 * $r)
    $g.FillEllipse($brush, $cx + 0.23 * $s - $r, $cy - 0.12 * $s - $r, 2 * $r, 2 * $r)
    Fill-RoundPolygon $g $color (0.06 * $s) @(
        ($cx - 0.48 * $s), ($cy - 0.04 * $s),
        ($cx + 0.48 * $s), ($cy - 0.04 * $s),
        $cx, ($cy + 0.44 * $s))
}

# 100x100 の座標で描いて、指定サイズに縮小する
function Render([string]$style, [int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 100, $size / 100)
    $small = $size -le 24
    $white = C '#FFFFFF'

    $colors = @{
        screen   = '#7B61FF', '#D14BE8'
        bookmark = '#14B8A6', '#2563EB'
        monogram = '#FF7A45', '#FF3D77'
    }[$style]
    $bg = New-RoundRect 6 6 88 88 22
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(6, 6), [System.Drawing.PointF]::new(94, 94), (C $colors[0]), (C $colors[1]))
    $g.FillPath($grad, $bg)

    switch ($style) {
        'screen' {
            # 自分だけのテレビ：画面＋スタンド＋お気に入りのハート
            $g.FillPath([System.Drawing.SolidBrush]::new($white), (New-RoundRect 19 25 62 43 9))
            $g.FillPath([System.Drawing.SolidBrush]::new($white), (New-RoundRect 37 73 26 6 3))
            Fill-RoundPolygon $g (C '#7B61FF') 5 @(44, 37, 44, 56, 60, 46.5)
            if (-not $small) {
                $g.FillEllipse([System.Drawing.SolidBrush]::new($white), 62, 56, 30, 30)
                $g.FillEllipse([System.Drawing.SolidBrush]::new((C '#FF4D8D')), 65, 59, 24, 24)
                Fill-Heart $g $white 77 71.5 13
            }
        }
        'bookmark' {
            # 再生ボタン＋しおり（保存した動画）
            Fill-RoundPolygon $g $white 9 @(36, 30, 36, 70, 70, 50)
            Fill-RoundPolygon $g (C '#FBBF24') 2 @(64, 6, 82, 6, 82, 34, 73, 27, 64, 34)
        }
        'monogram' {
            # M の文字＋再生バー
            $pen = [System.Drawing.Pen]::new($white, 11)
            $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
            $g.DrawLines($pen, [System.Drawing.PointF[]]@(
                    [System.Drawing.PointF]::new(26, 62), [System.Drawing.PointF]::new(26, 27),
                    [System.Drawing.PointF]::new(50, 48), [System.Drawing.PointF]::new(74, 27),
                    [System.Drawing.PointF]::new(74, 62)))
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), (New-RoundRect 22 72 56 6 3))
            $g.FillPath([System.Drawing.SolidBrush]::new($white), (New-RoundRect 22 72 34 6 3))
            if (-not $small) { $g.FillEllipse([System.Drawing.SolidBrush]::new($white), 51, 70, 10, 10) }
        }
    }
    $g.Dispose()
    $bmp
}

function Get-PngBytes($bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $ms.ToArray()
}

if ($Preview) {
    # 全デザインを、暗い背景・明るい背景で 256/48/32/16 の大きさで並べる
    $styles = 'screen', 'bookmark', 'monogram'
    $sheet = [System.Drawing.Bitmap]::new(3 * 420, 2 * 300)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.FillRectangle([System.Drawing.SolidBrush]::new((C '#202020')), 0, 0, 1260, 300)
    $g.FillRectangle([System.Drawing.SolidBrush]::new((C '#F3F3F3')), 0, 300, 1260, 300)
    for ($row = 0; $row -lt 2; $row++) {
        for ($i = 0; $i -lt 3; $i++) {
            $x = $i * 420 + 10; $y = $row * 300 + 22
            $g.DrawImage((Render $styles[$i] 256), $x, $y)
            $g.DrawImage((Render $styles[$i] 48), $x + 270, $y + 10)
            $g.DrawImage((Render $styles[$i] 32), $x + 270, $y + 80)
            $g.DrawImage((Render $styles[$i] 16), $x + 270, $y + 135)
            $label = @('1. screen', '2. bookmark', '3. monogram')[$i]
            $fg = if ($row -eq 0) { [System.Drawing.Brushes]::White } else { [System.Drawing.Brushes]::Black }
            $g.DrawString($label, [System.Drawing.Font]::new('Segoe UI', 11), $fg, $x + 270, $y + 170)
        }
    }
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    "preview: $Preview"
    return
}

# .ico（各サイズを PNG で格納）
New-Item -ItemType Directory -Force $OutDir | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (Get-PngBytes (Render $Style $s)) }

$icoPath = Join-Path $OutDir 'MyTube.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Dispose()

(Render $Style 256).Save((Join-Path $OutDir 'MyTube.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"icon: $icoPath ($Style)"
