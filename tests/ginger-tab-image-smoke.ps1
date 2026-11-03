# Synthetic bitmaps only: no screen capture, OCR language dependency or UI input.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/windows/ginger-new-matching-tab.ps1'))
$code=[regex]::Match($source,"(?s)using System.Drawing;.*?(?=\r?\n'@)").Value
if(-not $code){throw 'Tab image implementation missing'}
Add-Type -ReferencedAssemblies @([Drawing.Bitmap].Assembly.Location) -TypeDefinition $code
$bmp=[Drawing.Bitmap]::new(1900,60)
$g=[Drawing.Graphics]::FromImage($bmp)
try {
    $g.Clear([Drawing.Color]::LightGray)
    $g.FillRectangle([Drawing.Brushes]::Black,20,15,300,25)
    $trim=[GingerTabContrast]::TrimInk($bmp)
    try {
        if($trim.Width -ne 324 -or $trim.Height -ne 49){throw 'Text bounds or padding lost'}
        if($trim.GetPixel(12,12).R -ne 0){throw 'Text pixels lost'}
        if($trim.GetPixel(0,0).R -ne 255){throw 'OCR padding not white'}
    } finally {$trim.Dispose()}
    $g.Clear([Drawing.Color]::White)
    $rejected=$false
    try {$unexpected=[GingerTabContrast]::TrimInk($bmp); $unexpected.Dispose()} catch {$rejected=$true}
    if(-not $rejected){throw 'Empty frame accepted'}
    $g.Clear([Drawing.Color]::FromArgb(0,80,160))
    $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(180,180,180))
    try {$g.FillRectangle($brush,20,15,300,25)} finally {$brush.Dispose()}
    $contrast=[GingerTabContrast]::LightText($bmp)
    try {
        $trim=[GingerTabContrast]::TrimInk($contrast)
        try {if($trim.Width -ne 324 -or $trim.Height -ne 49){throw 'Active tab text lost'}} finally {$trim.Dispose()}
    } finally {$contrast.Dispose()}
} finally {$g.Dispose();$bmp.Dispose()}
Write-Output 'Tab image smoke checks passed'
