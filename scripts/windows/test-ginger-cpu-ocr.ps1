# Ginger4 CPU OCR smoke test; no GPU, clicks, SQL or network calls.
# Needs Tesseract OCR on the same Windows PC.
[CmdletBinding()]
param([string]$TesseractExe="", [string]$SupplierTitle="", [switch]$CaptureSupplierGrid)

Set-StrictMode -Version Latest
$ErrorActionPreference="Stop"
if($env:OS -ne "Windows_NT"){throw "Windows only"}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public sealed class GingerWindowRect {
 public long Hwnd; public int X,Y,W,H; public bool Visible; public string ClassName;
}
public static class GingerWin32Ocr {
 [StructLayout(LayoutKind.Sequential)]
 struct RECT {public int l,t,r,b;}
 delegate bool EnumProc(IntPtr h, IntPtr v);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetClassNameW")]
 static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h,EnumProc cb,IntPtr v);
 public static long ActiveHandle(){return GetForegroundWindow().ToInt64();}
 public static bool Activate(long hwnd){
   if(hwnd==0)return false;
   IntPtr h=new IntPtr(hwnd);
   ShowWindow(h,9); // SW_RESTORE; harmless window activation, no ERP writes
   return SetForegroundWindow(h);
 }
 public static uint ActivePid(){
   uint pid;GetWindowThreadProcessId(GetForegroundWindow(),out pid);return pid;
 }
 static GingerWindowRect Read(IntPtr h){
   RECT r;bool ok=GetWindowRect(h,out r);
   StringBuilder cls=new StringBuilder(256);GetClassName(h,cls,cls.Capacity);
   return new GingerWindowRect{Hwnd=h.ToInt64(),X=ok?r.l:0,Y=ok?r.t:0,
      W=ok?Math.Max(0,r.r-r.l):0,H=ok?Math.Max(0,r.b-r.t):0,
      Visible=IsWindowVisible(h),ClassName=cls.ToString()};
 }
 public static GingerWindowRect Active(){return Read(GetForegroundWindow());}
 public static List<GingerWindowRect> ListViews(long hwnd) {
   List<GingerWindowRect> result=new List<GingerWindowRect>();
   int seen=0;
   EnumProc cb=delegate(IntPtr h, IntPtr v){
     if(++seen>30000)return false;
     StringBuilder b=new StringBuilder(256);GetClassName(h,b,b.Capacity);
     if(b.ToString().IndexOf("SysListView32",StringComparison.OrdinalIgnoreCase)>=0)
       result.Add(Read(h));
     return true;
   };
   EnumChildWindows(new IntPtr(hwnd),cb,IntPtr.Zero);
   return result;
 }
}
'@


# The selected Ginger supplier NAME cell uses a LightBlue background,
# while ordinary name cells are pale yellow. Look for ONE horizontal band
# of this color in the first name column; don't mistake the tab for a row.
# This is only visual evidence, never an authoritative SKU identifier.
Add-Type -ReferencedAssemblies @([System.Drawing.Bitmap].Assembly.Location) -TypeDefinition @'
using System;
using System.Drawing;
using System.Collections.Generic;

public static class GingerSelectionBand
{
    private static bool IsLightBlue(Color px)
    {
        return px.R >= 140 && px.R <= 205
            && px.G >= 185 && px.G <= 235
            && px.B >= 205 && px.B <= 250
            && px.G > px.R + 22 && px.B > px.G + 5;
    }

    private static bool IsDarkBlue(Color px)
    {
        // Active selection on the newer Ginger supplier grid:
        // observed RGB(0,120,212), not the older LightBlue(173,216,230).
        return px.R <= 75 && px.G >= 65 && px.G <= 180
            && px.B >= 155 && px.B <= 250
            && px.B > px.G + 25 && px.G > px.R + 35;
    }

    private static bool IsSelectionBlue(Color px)
    {
        return IsLightBlue(px) || IsDarkBlue(px);
    }

    public static int NameCellRightEdge(string file, int selectedY)
    {
        using (Bitmap img = new Bitmap(file))
        {
            // Use the top interior of the blue row, clear of product text.
            // Detect the actual name-cell boundary instead of assuming 16.8%
            // image width (that cut off the TESTER suffix on wider grids).
            int y = Math.Max(0, Math.Min(img.Height - 1, selectedY + 3));
            int startX = (int)(img.Width * 0.06);
            int maxX = (int)(img.Width * 0.32);
            int lastBlue = -1;
            int nonBlueRun = 0;
            for (int x = startX; x < maxX; x++)
            {
                if (IsSelectionBlue(img.GetPixel(x, y)))
                {
                    lastBlue = x;
                    nonBlueRun = 0;
                }
                else if (lastBlue >= 0 && ++nonBlueRun >= 12)
                {
                    break;
                }
            }
            if (lastBlue < (int)(img.Width * 0.12)
                || lastBlue > (int)(img.Width * 0.30))
                return 0;
            return lastBlue;
        }
    }

    public static int[] Detect(string file)
    {
        using (Bitmap img = new Bitmap(file))
        {
            // Match only a blue NAME cell, not a full-width tab/pane bar.
            // On the real Ginger screenshot the blue bottom panel produced
            // a second "selected row" after 2x upscaling (the root bug).
            // Read the blank RIGHT side of the supplier NAME cell.
            // Older sampling (8.5%-14.5%) overlapped title glyphs:
            // a single row split into blue stripes above/below the text,
            // with neither stripe meeting the minimum height threshold.
            // On both observed Ginger layouts 15.1%-16.3% lies INSIDE the
            // selected name cell, just before the next table column.
            int x0 = (int)(img.Width * 0.151);
            int x1 = (int)(img.Width * 0.163);
            int guardX0 = (int)(img.Width * 0.245);
            int guardX1 = (int)(img.Width * 0.305);
            int y0 = Math.Min(25, img.Height / 10);
            int maxY = (int)(img.Height * 0.95);
            int step = Math.Max(3, (x1 - x0) / 12);
            int guardStep = Math.Max(3, (guardX1 - guardX0) / 12);
            List<int[]> bands = new List<int[]>();
            int start = -1;
            for (int y = y0; y < maxY; y++)
            {
                int count = 0;
                int matches = 0;
                for (int x = x0; x <= x1; x += step)
                {
                    if (IsSelectionBlue(img.GetPixel(x, y))) matches++;
                    count++;
                }
                int guardCount = 0;
                int guardMatches = 0;
                for (int x = guardX0; x <= guardX1; x += guardStep)
                {
                    if (IsSelectionBlue(img.GetPixel(x, y))) guardMatches++;
                    guardCount++;
                }
                // Actual selected name cells end before the price column;
                // full-width blue container bars are rejected.
                bool active = count > 0 && matches * 100 >= count * 65
                    && guardCount > 0 && guardMatches * 100 <= guardCount * 20;
                if (active && start < 0) start = y;
                if (!active && start >= 0)
                {
                    if (y - start >= 8 && y - start <= 70)
                        bands.Add(new int[] { start, y - start });
                    start = -1;
                }
            }
            // More than one highlight is ambiguous. Never select arbitrarily.
            if (bands.Count != 1) return new int[0];
            return bands[0];
        }
    }
}
'@

function Save-Region([System.Drawing.Rectangle]$Region,[string]$Path){
    $screen=[System.Windows.Forms.SystemInformation]::VirtualScreen
    $clip=[System.Drawing.Rectangle]::Intersect($Region,$screen)
    if($clip.Width -lt 80 -or $clip.Height -lt 50){throw "Region is offscreen or too small"}
    if(([int64]$clip.Width*$clip.Height) -gt 33177600){throw "Image too large"}
    $bmp=New-Object System.Drawing.Bitmap($clip.Width,$clip.Height)
    $gfx=[System.Drawing.Graphics]::FromImage($bmp)
    try{
        $gfx.CopyFromScreen($clip.Location,[System.Drawing.Point]::Empty,$clip.Size)
        # Scale 2x for the small 8-10pt text in WinForms listviews.
        $scaled=[System.Drawing.Bitmap]::new([int]($clip.Width*2),[int]($clip.Height*2))
        $drawing=[System.Drawing.Graphics]::FromImage($scaled)
        try {
            $drawing.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $drawing.DrawImage($bmp,0,0,$scaled.Width,$scaled.Height)
            $scaled.Save($Path,[System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $drawing.Dispose(); $scaled.Dispose() }
    }finally{$gfx.Dispose();$bmp.Dispose()}
}
function Run-Ocr([string]$Image,[string]$OutputBase,[string]$Language,[ValidateSet(6,7,11)][int]$Psm=6){
    # Tesseract writes UTF-8 on disk; avoids Windows PowerShell native encoding issues.
    $ocrArgs=@($Image,$OutputBase,"-l",$Language,"--psm",[string]$Psm)
    if($customTessdata){$ocrArgs+=@("--tessdata-dir",$customTessdata)}
    & $tesseract @ocrArgs 2>$null | Out-Null
    if($LASTEXITCODE -ne 0){throw "Tesseract failed for $Image"}
    $raw=[System.IO.File]::ReadAllText(($OutputBase+".txt"),[System.Text.Encoding]::UTF8)
    return @($raw -split "\r?\n" | Where-Object {-not [string]::IsNullOrWhiteSpace($_)} | Select-Object -First 60)
}
function Read-Grid-OcrSegments([string]$Image,[string]$OutputBase,[string]$Language){
    # TSV keeps words' positions rather than flattening grid columns into text.
    # Coordinates refer to the 2x PNG; they do not indicate selection.
    $argsList=@($Image,$OutputBase,"-l",$Language,"--psm","11")
    if($customTessdata){$argsList+=@("--tessdata-dir",$customTessdata)}
    # The per-user tessdata folder holds language weights, not configs/tsv.
    # Enable TSV via a built-in parameter; do not pass the missing "tsv" config.
    $argsList+=@("-c","tessedit_create_tsv=1","-c","tessedit_create_txt=0")
    & $tesseract @argsList 2>$null | Out-Null
    if($LASTEXITCODE -ne 0){throw "Tesseract could not read supplier-grid position data"}
    $tsvFile=$OutputBase+".tsv"
    if(-not (Test-Path -LiteralPath $tsvFile -PathType Leaf)){
        throw "Tesseract did not produce supplier-grid TSV"
    }
    $words=@(Import-Csv -LiteralPath $tsvFile -Delimiter "`t" -Encoding UTF8 |
        Where-Object {
            $_.level -eq "5" -and
            -not [string]::IsNullOrWhiteSpace($_.text) -and
            ($_.conf -as [double]) -ge 10
        })
    $segments=[System.Collections.Generic.List[object]]::new()
    foreach($group in ($words | Group-Object {
        "$($_.page_num):$($_.block_num):$($_.par_num):$($_.line_num)"
    })){
        $lineWords=@($group.Group | Sort-Object {[int]$_.left})
        $xmin=[int]::MaxValue; $ymin=[int]::MaxValue
        $xmax=0; $ymax=0; $confidenceSum=0.0
        foreach($word in $lineWords){
            $x=[int]$word.left; $y=[int]$word.top
            $xmin=[math]::Min($xmin,$x); $ymin=[math]::Min($ymin,$y)
            $xmax=[math]::Max($xmax,($x+[int]$word.width))
            $ymax=[math]::Max($ymax,($y+[int]$word.height))
            $confidenceSum+=[double]$word.conf
        }
        $lineText=($lineWords | ForEach-Object {$_.text.Trim()}) -join " "
        if(-not [string]::IsNullOrWhiteSpace($lineText)){
            $segments.Add([pscustomobject]@{
                x=$xmin; y=$ymin; width=($xmax-$xmin); height=($ymax-$ymin)
                confidence=[math]::Round(($confidenceSum/$lineWords.Count),1)
                text=$lineText.Substring(0,[math]::Min(350,$lineText.Length))
            })
        }
    }
    return @($segments.ToArray() | Sort-Object y,x | Select-Object -First 120)
}
$tesseract=$TesseractExe
if(-not $tesseract){
    $cmd=Get-Command tesseract.exe -ErrorAction SilentlyContinue
    if($null -ne $cmd){$tesseract=$cmd.Source}
}
if(-not $tesseract){
    $path=Join-Path $env:ProgramFiles "Tesseract-OCR\tesseract.exe"
    if(Test-Path -LiteralPath $path){$tesseract=$path}
}
if(-not $tesseract -or -not (Test-Path -LiteralPath $tesseract -PathType Leaf)){
    throw "Tesseract missing. Install: winget install -e --id UB-Mannheim.TesseractOCR; include Russian language (rus)."
}
# Prefer language data in the user's profile. The setup helper installs
# eng+rus there without administrator access to Program Files.
$customTessdata=Join-Path $env:LOCALAPPDATA "BabyAI\Tesseract\tessdata"
if(-not ((Test-Path -LiteralPath (Join-Path $customTessdata "eng.traineddata") -PathType Leaf) -and
          (Test-Path -LiteralPath (Join-Path $customTessdata "rus.traineddata") -PathType Leaf))){
    $customTessdata=""
}
$listArgs=@("--list-langs")
if($customTessdata){$listArgs+=@("--tessdata-dir",$customTessdata)}
$languages=@(& $tesseract @listArgs 2>&1 | ForEach-Object {"$_".Trim().ToLowerInvariant()})
if($LASTEXITCODE -ne 0 -or $languages -notcontains "eng"){throw "Tesseract must have English OCR data (eng)."}
$lang="eng"
if($languages -contains "rus"){$lang="eng+rus"}
# A PowerShell command starts with Windows Terminal in the foreground.
# Locate Ginger4.exe by real process identity, then activate its main window.
# This does not click any controls or modify the Ginger database.
$gingerProcesses=@(Get-Process -Name "Ginger4" -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
if($gingerProcesses.Count -eq 0){throw "Ginger4 is not open. Start Ginger and open the supplier matching page."}
if($gingerProcesses.Count -gt 1){throw "More than one Ginger4 main window found. Close extra instances for a safe test."}
$ginger=$gingerProcesses[0]
$gingerHwnd=[int64]$ginger.MainWindowHandle
if([GingerWin32Ocr]::ActivePid() -ne [uint32]$ginger.Id){
    Write-Host "Switching from PowerShell to Ginger4 window..."
    [GingerWin32Ocr]::Activate($gingerHwnd) | Out-Null
    Start-Sleep -Milliseconds 750
}
if([GingerWin32Ocr]::ActivePid() -ne [uint32]$ginger.Id){
    Write-Host "Windows prevented automatic switching. Click the Ginger window now (4 seconds)."
    Start-Sleep -Seconds 4
}
$start=[GingerWin32Ocr]::Active()
$pidOfWindow=[GingerWin32Ocr]::ActivePid()
if($start.Hwnd -eq 0 -or $pidOfWindow -ne [uint32]$ginger.Id){
    throw "Ginger4 did not become active. Open its supplier matching page and retry."
}
if($start.W -lt 700 -or $start.H -lt 440){throw "Maximize Ginger4 to show both bottom tables."}
$views=[GingerWin32Ocr]::ListViews($start.Hwnd)
$possible=@($views.ToArray() | Where-Object {
    $_.Visible -and $_.W -gt 180 -and $_.H -gt 50 -and
    $_.Y -gt ($start.Y+($start.H*0.55))
} | Sort-Object {-($_.W*$_.H)} | Select-Object -First 2)
$mode="native_listview_regions"
if($possible.Count -eq 2){
    $sorted=@($possible | Sort-Object X)
    $left=[System.Drawing.Rectangle]::new($sorted[0].X,$sorted[0].Y,$sorted[0].W,$sorted[0].H)
    $right=[System.Drawing.Rectangle]::new($sorted[1].X,$sorted[1].Y,$sorted[1].W,$sorted[1].H)
}else{
    $mode="approximate_bottom_regions"
    $half=[int]($start.W/2)
    $y=$start.Y+[int]($start.H*0.73)
    $h=[int]($start.H*0.24)
    $left=[System.Drawing.Rectangle]::new(($start.X+2),$y,($half-4),$h)
    $right=[System.Drawing.Rectangle]::new(($start.X+$half+2),$y,($start.W-$half-4),$h)
}
$folder=Join-Path (Get-Location).Path ("ginger-cpu-test-"+(Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$leftPng=Join-Path $folder "matching-left.png"
$rightPng=Join-Path $folder "catalogue-right.png"
Save-Region $left $leftPng
Save-Region $right $rightPng
if([GingerWin32Ocr]::ActiveHandle() -ne $start.Hwnd -or
   [GingerWin32Ocr]::ActivePid() -ne $pidOfWindow) {
    Remove-Item -LiteralPath $leftPng,$rightPng -Force -ErrorAction SilentlyContinue
    throw "Window changed during capture. Cancelled."
}
$leftLines=@(Run-Ocr $leftPng (Join-Path $folder "matching-left") $lang)
$rightLines=@(Run-Ocr $rightPng (Join-Path $folder "catalogue-right") $lang)
# The supplier grid uses a different, non-ListView WinForms control.
# Capture its upper area only with an explicit opt-in. Its OCR lines are
# NOT considered the selected supplier row or business item identity.
$supplierGridImage=$null
$supplierGridLines=@()
$supplierGridSegments=@()
$selectedRowImage=$null
$selectedRowVisualCandidate=$null
$selectedRowBandY=$null
$selectedRowBandHeight=$null
if($CaptureSupplierGrid){
    if([GingerWin32Ocr]::ActiveHandle() -ne $start.Hwnd -or
       [GingerWin32Ocr]::ActivePid() -ne $pidOfWindow) {
        throw "Ginger changed before supplier-grid capture. Cancelled."
    }
    $gridTop=$start.Y+[int]($start.H*0.16)
    $gridBottom=[math]::Min($left.Y,$right.Y)-10
    $gridHeight=[int]($gridBottom-$gridTop)
    if($gridHeight -lt 80){throw "Supplier grid region is too small; maximize Ginger."}
    $gridRegion=[System.Drawing.Rectangle]::new(($start.X+4),$gridTop,($start.W-8),$gridHeight)
    $supplierGridImage=Join-Path $folder "supplier-grid.png"
    Save-Region $gridRegion $supplierGridImage
    if([GingerWin32Ocr]::ActiveHandle() -ne $start.Hwnd -or
       [GingerWin32Ocr]::ActivePid() -ne $pidOfWindow) {
        Remove-Item -LiteralPath $supplierGridImage -Force -ErrorAction SilentlyContinue
        throw "Ginger changed during supplier-grid capture. Cancelled."
    }
    # Sparse-text mode copes better with many columns than the list-view mode.
    $supplierGridBase=Join-Path $folder "supplier-grid"
    $supplierGridLines=@(Run-Ocr $supplierGridImage $supplierGridBase $lang 11)
    $supplierGridSegments=@(Read-Grid-OcrSegments $supplierGridImage $supplierGridBase $lang)
    $selectionBand=[GingerSelectionBand]::Detect($supplierGridImage)
    if($selectionBand.Length -eq 2){
        $selectedRowBandY=[int]$selectionBand[0]
        $selectedRowBandHeight=[int]$selectionBand[1]
        $selectedRowImage=Join-Path $folder "selected-supplier-row.png"
        $fullImage=[System.Drawing.Bitmap]::FromFile($supplierGridImage)
        try{
            $cropX=[int]($fullImage.Width*0.008)
            # Crop inside the selected band to exclude neighboring rows.
            $cropY=[int]($selectedRowBandY+4)
            $cropH=[int]($selectedRowBandHeight-8)
            # Determine the actual right edge of the selected NAME cell.
            # A fixed percentage truncated long titles such as ... TESTER.
            $nameCellEnd=[GingerSelectionBand]::NameCellRightEdge(
                $supplierGridImage, $selectedRowBandY)
            if($nameCellEnd -eq 0){
                throw "Selected supplier name cell boundary is ambiguous"
            }
            $cropW=[int]($nameCellEnd-$cropX-5)
            if($cropY -lt 0 -or $cropH -lt 14 -or $cropW -lt 120 -or
               ($cropY+$cropH) -gt $fullImage.Height -or
               ($cropX+$cropW) -gt $fullImage.Width){
                throw "Detected supplier row is too short or outside the captured image"
            }
            $rectangle=[System.Drawing.Rectangle]::new(
                [int]$cropX,[int]$cropY,[int]$cropW,[int]$cropH)
            $singleRow=$fullImage.Clone($rectangle,$fullImage.PixelFormat)
            try{$singleRow.Save($selectedRowImage,[System.Drawing.Imaging.ImageFormat]::Png)}
            finally{$singleRow.Dispose()}
        } finally{$fullImage.Dispose()}
        $selectedLines=@(Run-Ocr $selectedRowImage (Join-Path $folder "selected-supplier-row") $lang 7)
        if($selectedLines.Count -gt 0){
            $selectedRowVisualCandidate=($selectedLines -join " ").Trim()
        }
    }
}
$warnings=[System.Collections.Generic.List[string]]::new()
$warnings.Add("OCR results are unverified; candidate names may contain look-alike Cyrillic/Latin characters and truncated sizes.")
if($CaptureSupplierGrid){
    $warnings.Add("Supplier-grid selection is based on a blue pixel band, not a confirmed Ginger item ID.")
    if($null -eq $selectedRowBandY){
        $warnings.Add("No unique LightBlue selected NAME cell detected; cannot identify selected supplier row.")
    } elseif([string]::IsNullOrWhiteSpace($selectedRowVisualCandidate)){
        $warnings.Add("Selected LightBlue row detected, but OCR of its isolated name cell returned no title.")
    }
    if($supplierGridLines.Count -eq 0){
        $warnings.Add("No text could be recognized in the supplier grid.")
    }
}
if($mode -ne "native_listview_regions"){$warnings.Add("Using approximate bottom regions; check screenshots.")}
if($lang -eq "eng"){$warnings.Add("Russian OCR (rus) is missing; Cyrillic may be wrong.")}
if($leftLines.Count -eq 0){$warnings.Add("Nothing readable in left matching list.")}
if($rightLines.Count -eq 0){$warnings.Add("Nothing readable in right catalogue list.")}
if($SupplierTitle){
    foreach($term in @("intense","qahwa","elixir","extreme","absolu")){
        if($SupplierTitle -match ("(?i)\b"+$term+"\b")){
            $candidateText=($rightLines -join " ")
            if($candidateText -notmatch ("(?i)\b"+$term+"\b")){
                $warnings.Add("Supplier variant $term not found on right: do NOT link to base fragrance.")
            }
        }
    }
}
$hint=$null
if($SupplierTitle){$hint=$SupplierTitle}
$dataDirLabel="installed_default"
if($customTessdata){$dataDirLabel=$customTessdata}
$report=[ordered]@{
    source="Ginger4 CPU OCR pilot"
    engine="Tesseract CPU"
    image_upscale_factor=2
    language_data_directory=$dataDirLabel
    read_only=$true
    ginger_modified=$false
    local_only=$true
    language=$lang
    capture_mode=$mode
    native_listviews_found=$views.Count
    supplier_title_hint=$hint
    left_search_words=$leftLines
    right_catalogue_candidates=$rightLines
    supplier_grid_ocr_requested=[bool]$CaptureSupplierGrid
    supplier_grid_lines=$supplierGridLines
    supplier_grid_segments=$supplierGridSegments
    supplier_grid_segment_coordinates="relative_to_2x_supplier_grid_png"
    supplier_grid_image_local=$supplierGridImage
    selected_supplier_row_verified=$false
    selected_row_color_band_detected=($null -ne $selectedRowBandY)
    selected_row_band_y_in_2x_png=$selectedRowBandY
    selected_row_band_height_in_2x_png=$selectedRowBandHeight
    selected_row_visual_candidate=$selectedRowVisualCandidate
    selected_row_image_local=$selectedRowImage
    safe_for_automatic_matching=$false
    warnings=$warnings.ToArray()
    images_local=@($leftPng,$rightPng)
    recommendation="review_only_no_linking"
}
$reportPath=Join-Path $folder "ginger-cpu-report.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "CPU OCR completed: $reportPath"
Write-Host "Left lines: $($leftLines.Count); right lines: $($rightLines.Count); upper lines: $($supplierGridLines.Count); mode: $mode; languages: $lang"
Write-Host "Read-only: no clicks, mapping, network or SQL operations."
Write-Host "Review report for sensitive supplier/product names before sharing."
