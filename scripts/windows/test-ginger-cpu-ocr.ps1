# Ginger4 CPU OCR smoke test; no GPU, clicks, SQL or network calls.
# Needs Tesseract OCR on the same Windows PC.
[CmdletBinding()]
param([string]$TesseractExe="", [string]$SupplierTitle="")

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

function Save-Region([System.Drawing.Rectangle]$Region,[string]$Path){
    $screen=[System.Windows.Forms.SystemInformation]::VirtualScreen
    $clip=[System.Drawing.Rectangle]::Intersect($Region,$screen)
    if($clip.Width -lt 80 -or $clip.Height -lt 50){throw "Region is offscreen or too small"}
    if(([int64]$clip.Width*$clip.Height) -gt 33177600){throw "Image too large"}
    $bmp=New-Object System.Drawing.Bitmap($clip.Width,$clip.Height)
    $gfx=[System.Drawing.Graphics]::FromImage($bmp)
    try{
        $gfx.CopyFromScreen($clip.Location,[System.Drawing.Point]::Empty,$clip.Size)
        $bmp.Save($Path,[System.Drawing.Imaging.ImageFormat]::Png)
    }finally{$gfx.Dispose();$bmp.Dispose()}
}
function Run-Ocr([string]$Image,[string]$OutputBase,[string]$Language){
    # Tesseract writes UTF-8 on disk; avoids Windows PowerShell native encoding issues.
    & $tesseract $Image $OutputBase "-l" $Language "--psm" "6" 2>$null | Out-Null
    if($LASTEXITCODE -ne 0){throw "Tesseract failed for $Image"}
    $raw=[System.IO.File]::ReadAllText(($OutputBase+".txt"),[System.Text.Encoding]::UTF8)
    return @($raw -split "\r?\n" | Where-Object {-not [string]::IsNullOrWhiteSpace($_)} | Select-Object -First 60)
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
$languages=@(& $tesseract "--list-langs" 2>&1 | ForEach-Object {"$_".Trim().ToLowerInvariant()})
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
$warnings=[System.Collections.Generic.List[string]]::new()
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
$report=[ordered]@{
    source="Ginger4 CPU OCR pilot"
    engine="Tesseract CPU"
    read_only=$true
    ginger_modified=$false
    local_only=$true
    language=$lang
    capture_mode=$mode
    native_listviews_found=$views.Count
    supplier_title_hint=$hint
    left_search_words=$leftLines
    right_catalogue_candidates=$rightLines
    warnings=$warnings.ToArray()
    images_local=@($leftPng,$rightPng)
    recommendation="review_only_no_linking"
}
$reportPath=Join-Path $folder "ginger-cpu-report.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "CPU OCR completed: $reportPath"
Write-Host "Left lines: $($leftLines.Count); right lines: $($rightLines.Count); mode: $mode; languages: $lang"
Write-Host "Read-only: no clicks, mapping, network or SQL operations."
Write-Host "Review report for sensitive supplier/product names before sharing."
