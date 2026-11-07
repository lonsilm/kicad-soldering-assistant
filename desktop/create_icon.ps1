param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
function Rounded-Path([single]$X,[single]$Y,[single]$W,[single]$H,[single]$R) {
    $path=New-Object Drawing.Drawing2D.GraphicsPath
    $d=$R*2
    $path.AddArc($X,$Y,$d,$d,180,90)
    $path.AddArc($X+$W-$d,$Y,$d,$d,270,90)
    $path.AddArc($X+$W-$d,$Y+$H-$d,$d,$d,0,90)
    $path.AddArc($X,$Y+$H-$d,$d,$d,90,90)
    $path.CloseFigure()
    return $path
}
$frames=New-Object 'Collections.Generic.List[byte[]]'
$sizes=@(16,32,48,256)
foreach ($size in $sizes) {
    $bitmap=New-Object Drawing.Bitmap($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode='AntiAlias'
    $graphics.ScaleTransform($size/256.0,$size/256.0)
    $navy=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(248,229,219))
    $green=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(163,74,55))
    $white=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255,253,250))
    $gold=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(115,100,149))
    $path=Rounded-Path 4 4 248 248 52
    $graphics.FillPath($navy,$path)
    $path.Dispose()
    $chip=Rounded-Path 62 62 132 132 15
    $pen=New-Object Drawing.Pen([Drawing.Color]::FromArgb(163,74,55),11)
    $graphics.DrawPath($pen,$chip)
    foreach ($position in @(84,118,152)) {
        $graphics.FillRectangle($green,$position,42,16,22)
        $graphics.FillRectangle($green,$position,192,16,22)
        $graphics.FillRectangle($green,42,$position,22,16)
        $graphics.FillRectangle($green,192,$position,22,16)
    }
    $trace=New-Object Drawing.Pen([Drawing.Color]::FromArgb(255,253,250),15)
    $trace.StartCap='Round';$trace.EndCap='Round'
    $graphics.DrawLines($trace,[Drawing.PointF[]]@((New-Object Drawing.PointF(96,103)),(New-Object Drawing.PointF(122,103)),(New-Object Drawing.PointF(122,148)),(New-Object Drawing.PointF(153,148))))
    $graphics.FillEllipse($gold,139,134,29,29)
    $memory=New-Object IO.MemoryStream
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($memory.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $OutputDirectory 'app.png'),[Drawing.Imaging.ImageFormat]::Png) }
    foreach ($object in @($memory,$trace,$pen,$chip,$navy,$green,$white,$gold,$graphics,$bitmap)) { $object.Dispose() }
}
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'app.ico'))
$writer=New-Object IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for ($i=0;$i -lt $sizes.Count;$i++) {
        $dimension=if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
        $writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose();$stream.Dispose() }
