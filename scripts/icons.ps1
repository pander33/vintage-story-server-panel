# Значок повторяет CSS brand-mark из панели: тёмная тень, повёрнутый квадрат
# с тонкой медной рамкой и буква V жирным Georgia. Запуск на Windows:
#   ./scripts/icons.ps1
# SVG, PNG и ICO создаются из одной геометрии и одного контура буквы.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$static = Join-Path $root 'src/Panel.Web/Static'
$colors = @{
	Background = '#1c1613'
	Shadow = '#30231c'
	Fill = '#3b2920'
	Border = '#a74b1c'
	Letter = '#d35400'
}
$center = 36.0
$canvasSize = 72.0
$culture = [System.Globalization.CultureInfo]::InvariantCulture

function New-Diamond([double] $side) {
	$reach = $side / [Math]::Sqrt(2)
	[System.Drawing.PointF]::new($center, $center - $reach)
	[System.Drawing.PointF]::new($center + $reach, $center)
	[System.Drawing.PointF]::new($center, $center + $reach)
	[System.Drawing.PointF]::new($center - $reach, $center)
}

function Number([double] $value) {
	return $value.ToString('0.###', $culture)
}

function Points-Path([System.Drawing.PointF[]] $points) {
	return 'M' + (($points | ForEach-Object { "$(Number $_.X) $(Number $_.Y)" }) -join ' ') + 'Z'
}

function Letter-Path([System.Drawing.Drawing2D.GraphicsPath] $path) {
	$points = $path.PathPoints
	$types = $path.PathTypes
	$parts = [System.Collections.Generic.List[string]]::new()
	for ($i = 0; $i -lt $points.Length; $i++) {
		$type = $types[$i] -band 7
		if ($type -eq 0) {
			$parts.Add("M$(Number $points[$i].X) $(Number $points[$i].Y)")
		} elseif ($type -eq 1) {
			$parts.Add("L$(Number $points[$i].X) $(Number $points[$i].Y)")
		} elseif ($type -eq 3) {
			$end = $i + 2
			$parts.Add("C$(Number $points[$i].X) $(Number $points[$i].Y) $(Number $points[$i + 1].X) $(Number $points[$i + 1].Y) $(Number $points[$end].X) $(Number $points[$end].Y)")
			$i = $end
		}
		if (($types[$i] -band 128) -ne 0) { $parts.Add('Z') }
	}
	return $parts -join ' '
}

$shadow = [System.Drawing.PointF[]](New-Diamond 47)
$square = [System.Drawing.PointF[]](New-Diamond 37)
$letter = [System.Drawing.Drawing2D.GraphicsPath]::new()
$family = [System.Drawing.FontFamily]::new('Georgia')
$letter.AddString('V', $family, [int][System.Drawing.FontStyle]::Bold, 22,
	[System.Drawing.PointF]::new(0, 0), [System.Drawing.StringFormat]::GenericDefault)
$family.Dispose()
$bounds = $letter.GetBounds()
$move = [System.Drawing.Drawing2D.Matrix]::new()
$move.Translate([single]($center - $bounds.Left - $bounds.Width / 2),
	[single]($center - $bounds.Top - $bounds.Height / 2))
$letter.Transform($move)
$move.Dispose()

$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 72 72">
	<path d="$(Points-Path $shadow)" fill="$($colors.Shadow)"/>
	<path d="$(Points-Path $square)" fill="$($colors.Fill)" stroke="$($colors.Border)" stroke-width="1" stroke-linejoin="miter"/>
	<path d="$(Letter-Path $letter)" fill="$($colors.Letter)"/>
</svg>
"@
[System.IO.File]::WriteAllText((Join-Path $static 'favicon.svg'),
	$svg + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

function New-Icon([int] $size, [string] $background) {
	$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
	$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$graphics.Clear([System.Drawing.Color]::Transparent)
	$scale = $size / $canvasSize
	if ($background) {
		$graphics.Clear([System.Drawing.ColorTranslator]::FromHtml($background))
		# iOS скругляет углы значка, поэтому вокруг копии оставлен отступ.
		$graphics.TranslateTransform($size * 0.05, $size * 0.05)
		$scale *= 0.9
	}
	$graphics.ScaleTransform($scale, $scale)

	$shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($colors.Shadow))
	$fillBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($colors.Fill))
	$letterBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($colors.Letter))
	$borderPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($colors.Border), 1)
	$borderPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
	$graphics.FillPolygon($shadowBrush, $shadow)
	$graphics.FillPolygon($fillBrush, $square)
	$graphics.DrawPolygon($borderPen, $square)
	$graphics.FillPath($letterBrush, $letter)
	$shadowBrush.Dispose(); $fillBrush.Dispose(); $letterBrush.Dispose(); $borderPen.Dispose()
	$graphics.Dispose()

	$stream = [System.IO.MemoryStream]::new()
	$bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
	$bitmap.Dispose()
	$bytes = $stream.ToArray()
	$stream.Dispose()
	return , $bytes
}

# ICO с PNG внутри: все кадры показывают тот же знак без варианта рисунка.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-Icon $size $null) }
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)
$writer.Write([uint16] 0); $writer.Write([uint16] 1); $writer.Write([uint16] $sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
	$side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
	$writer.Write([byte] $side); $writer.Write([byte] $side); $writer.Write([byte] 0); $writer.Write([byte] 0)
	$writer.Write([uint16] 1); $writer.Write([uint16] 32)
	$writer.Write([uint32] $images[$i].Length); $writer.Write([uint32] $offset)
	$offset += $images[$i].Length
}
foreach ($iconImage in $images) { $writer.Write($iconImage) }
$writer.Flush()

New-Item -ItemType Directory -Force (Join-Path $root 'assets') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $root 'assets/vs-panel.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $static 'favicon.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $static 'apple-touch-icon.png'), (New-Icon 180 $colors.Background))
[System.IO.File]::WriteAllBytes((Join-Path $static 'favicon-32.png'), (New-Icon 32 $null))
$writer.Dispose()
$ico.Dispose()
$letter.Dispose()
