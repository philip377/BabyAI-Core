# Experimental local visual navigation. No product matching or catalogue writes.
[CmdletBinding()]
param([switch]$ConfirmNavigation)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (-not $ConfirmNavigation) { throw 'Navigation and local screen observation confirmation required' }
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
Invoke-Expression $source.Substring(0,$source.IndexOf('$processes = @('))
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies @([Drawing.Bitmap].Assembly.Location) -TypeDefinition @'
using System.Drawing;
public static class GingerTabContrast {
    public static Bitmap LightText(Bitmap source) {
        Bitmap result=new Bitmap(source.Width,source.Height);
        for(int y=0;y<source.Height;y++) for(int x=0;x<source.Width;x++) {
            Color c=source.GetPixel(x,y);
            result.SetPixel(x,y,c.R>210 && c.G>210 && c.B>210 ? Color.Black : Color.White);
        }
        return result;
    }
}
'@
$targets=@(Get-Process Ginger4 -ErrorAction Stop | Where-Object MainWindowHandle -ne 0)
if($targets.Count -ne 1) {throw 'Exactly one Ginger window required'}
$window=$targets[0].MainWindowHandle
$processId=[uint32]$targets[0].Id
[GingerMenuAccess]::ActivateForRead($window,$processId)
$expectedDocument=[GingerMenuAccess]::ReadPriceDocument($window,$processId)
$directory=Join-Path $env:LOCALAPPDATA ('BabyAI/diagnostics/ginger-tabs-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
function Read-TabFrame($Tab,[string]$Name) {
    [GingerMenuAccess]::RequireForeground($window,$processId)
    $bmp=[Drawing.Bitmap]::new($Tab.Width,$Tab.Height)
    $graphics=[Drawing.Graphics]::FromImage($bmp)
    $scaled=[Drawing.Bitmap]::new($Tab.Width*3,$Tab.Height*3)
    $drawing=[Drawing.Graphics]::FromImage($scaled)
    $memory=[IO.MemoryStream]::new()
    try {
        $graphics.CopyFromScreen($Tab.X,$Tab.Y,0,0,$bmp.Size)
        [GingerMenuAccess]::RequireForeground($window,$processId)
        $drawing.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $drawing.DrawImage($bmp,0,0,$scaled.Width,$scaled.Height)
        $scaled.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
        $bytes=$memory.ToArray()
        $path=Join-Path $directory ($Name+'.png')
        [IO.File]::WriteAllBytes($path,$bytes)
        $hash=[Security.Cryptography.SHA256]::Create()
        try {$digest=[Convert]::ToBase64String($hash.ComputeHash($bytes))} finally {$hash.Dispose()}
        $blue=0; $samples=0
        for($y=2;$y -lt $bmp.Height-2;$y+=2) {
            for($x=4;$x -lt $bmp.Width-4;$x+=5) {
                $p=$bmp.GetPixel($x,$y); $samples++
                if($p.B -gt $p.R+30 -and $p.B -gt $p.G+15) {$blue++}
            }
        }
        $ratio=$blue/[double][Math]::Max(1,$samples)
        $ocrPath=$path
        if($ratio -ge 0.5) {
            $contrast=[GingerTabContrast]::LightText($scaled)
            try {$ocrPath=Join-Path $directory ($Name+'-ocr.png'); $contrast.Save($ocrPath,[Drawing.Imaging.ImageFormat]::Png)} finally {$contrast.Dispose()}
        }
        [pscustomobject]@{path=$path;ocr_path=$ocrPath;hash=$digest;blue_ratio=$ratio}
    } finally {$memory.Dispose();$drawing.Dispose();$scaled.Dispose();$graphics.Dispose();$bmp.Dispose()}
}
function Require-PriceDocument {
    $document=[GingerMenuAccess]::ReadPriceDocument($window,$processId)
    if($document.Number -ne $expectedDocument.Number -or $document.Supplier -ne $expectedDocument.Supplier){throw 'Supplier price document changed'}
}
Require-PriceDocument
$candidates=@([GingerMenuAccess]::TabCandidates($window,$processId))
if($candidates.Count -ne 3){throw 'One three-tab group required'}
$readings=@()
for($i=0;$i -lt $candidates.Count;$i++) {
    $frame=Read-TabFrame $candidates[$i] ('before-'+$i)
    $json=& (Join-Path $PSScriptRoot 'ginger-windows-ocr.ps1') -ImagePath $frame.ocr_path
    $lines=ConvertFrom-Json -InputObject ($json -join [Environment]::NewLine)
    $texts=@(foreach($line in $lines){if($null -ne $line){$line.text}})
    $label=([GingerMenuAccess]::Normalize(($texts -join ' ')) -replace '\s+',' ').Replace('—','-').Replace('–','-')
    $readings+= [pscustomobject]@{tab=$candidates[$i];frame=$frame;label=$label;index=$i}
}
$selected=@($readings | Where-Object label -eq 'сопоставление - новые')
if($selected.Count -ne 1){throw 'Exact new matching tab label not uniquely recognized; no click'}
Require-PriceDocument
foreach($reading in $readings) {
    $fresh=Read-TabFrame $reading.tab ('recheck-'+$reading.index)
    if($fresh.hash -ne $reading.frame.hash){throw 'Tab pixels changed after OCR; no click'}
}
$target=$selected[0]
$alreadySelected=$target.frame.blue_ratio -ge 0.5
if(-not $alreadySelected){[GingerMenuAccess]::ClickVerifiedTab($window,$processId,$target.tab,$true)}
Start-Sleep -Milliseconds 500
$after=Read-TabFrame $target.tab 'after'
$othersBlue=@($readings | Where-Object { $_.index -ne $target.index } | ForEach-Object { (Read-TabFrame $_.tab ('after-'+$_.index)).blue_ratio } | Where-Object {$_ -ge 0.5})
if($after.blue_ratio -lt 0.5 -or $othersBlue.Count -ne 0){throw 'Selected-tab appearance not verified; no retry'}
[GingerMenuAccess]::RequireWindow($window,$processId)
Require-PriceDocument
[pscustomobject]@{status='new_matching_tab_visually_selected';clicked=(-not $alreadySelected);can_match=$false;document_number_and_supplier_stable=$true;diagnostics=$directory} | ConvertTo-Json
