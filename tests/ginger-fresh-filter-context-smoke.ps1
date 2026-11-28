# Execute the production pre-filter fragment and extracted native comparisons.
# Readers/input are synthetic; no live process lookup, driver import or UI access.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$native=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/windows/ginger-navigation.ps1') -Raw -Encoding UTF8
function Definition([string]$Signature) {
 $start=$native.IndexOf($Signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Production definition missing: $Signature"}
 $opening=$native.IndexOf('{',$start);$depth=0
 for($index=$opening;$index -lt $native.Length;$index++) {
  if($native[$index] -eq '{'){$depth++}
  elseif($native[$index] -eq '}') {
   $depth--
   if($depth -eq 0){return $native.Substring($start,$index-$start+1)}
  }
 }
 throw "Unclosed production definition: $Signature"
}
$classes=@(
 'public sealed class GingerSelectedProduct',
 'public sealed class GingerCandidateSnapshot',
 'public sealed class GingerSearchContext',
 'public sealed class GingerFilterOnlyContext'
) | ForEach-Object { Definition $_ }
$methods=@(
 'public static void RequireSameSelectedProduct(',
 'private static bool SameBounds(',
 'public static void RequireSameCandidates(',
 'public static void RequireSameSearchContext('
) | ForEach-Object { Definition $_ }
$code=@'
using System;
using System.Collections.Generic;
public sealed class GingerCatalogueCandidate {
 public int ObservedPosition,StateImage;
 public string Title,DescriptionText,DefaultAction;
 public bool Selected,IdentityVerified;
 public bool? Checked;
 public int[] Bounds;
}
'@ + ($classes -join "`n") + @'

public static class GingerMenuAccess {
 public static string Mode;
 public static int FullReads,FilterReads,Inputs;
 public static List<string> Events=new List<string>();
 public static void Reset(string mode) {Mode=mode;FullReads=FilterReads=Inputs=0;Events.Clear();}
 private static GingerSelectedProduct Source() {
  return new GingerSelectedProduct {TableHandle=42,ObservedRowPosition=3,DocumentNumber="document",
   Supplier="supplier",Title="LM White Label ALDHEYX 100ml EDP",PriceText="88",LinkedProductText="none"};
 }
 private static GingerCatalogueCandidate Row(int position,string title,bool word) {
  return new GingerCatalogueCandidate {ObservedPosition=position,Title=title,DescriptionText="search "+title,
   DefaultAction="double click",Checked=word?(bool?)false:null,Bounds=new[]{10,position*20,200,20}};
 }
 public static GingerSearchContext ExpectedFull() {
  return new GingerSearchContext {QueryWordsMatchSource=true,
   SearchWords=new GingerCandidateSnapshot {Source=Source(),PanelHandle=10,
    Candidates=new[]{Row(1,"LM",true),Row(2,"ALDHEYX",true)}},
   Products=new GingerCandidateSnapshot {Source=Source(),PanelHandle=20,
    Candidates=Mode=="empty"?new GingerCatalogueCandidate[0]:new[]{Row(1,"Product A",false),Row(2,"Product B",false)}}};
 }
 public static GingerFilterOnlyContext ExpectedFilter() {
  return new GingerFilterOnlyContext {SearchWords=ExpectedFull().SearchWords,
   QueryWordsMatchSource=true,ProductPanelHandle=20,ProductRows=278};
 }
 private static void ChangeWords(GingerCandidateSnapshot words) {
  if(Mode=="word_title")words.Candidates[1].Title="Other";
  if(Mode=="word_search")words.Candidates[1].DescriptionText="Other synonyms";
  if(Mode=="word_checked"){words.Candidates[1].Checked=true;words.Candidates[1].StateImage=2;}
  if(Mode=="word_panel")words.PanelHandle=11;
  if(Mode=="source_table")words.Source.TableHandle=43;
  if(Mode=="source_position")words.Source.ObservedRowPosition=4;
  if(Mode=="source_document")words.Source.DocumentNumber="other";
  if(Mode=="source_supplier")words.Source.Supplier="other";
  if(Mode=="source_title")words.Source.Title="other";
  if(Mode=="source_price")words.Source.PriceText="89";
  if(Mode=="source_linked")words.Source.LinkedProductText="linked";
 }
 private static void ReaderGuard(IntPtr window,uint owner,int pause) {
  if(window!=new IntPtr(7) || owner!=8 || pause!=700)throw new Exception("Reader binding or settling pause changed");
  if(Mode=="read_error")throw new InvalidOperationException("Synthetic read failed");
 }
 public static GingerSearchContext ReadSearchContext(IntPtr window,uint owner,int pause) {
  FullReads++;Events.Add("full");ReaderGuard(window,owner,pause);
  if(Mode=="became_overflow")throw new InvalidOperationException("Candidate read limit exceeded");
  var result=ExpectedFull();ChangeWords(result.SearchWords);
  if(Mode=="lexical")result.QueryWordsMatchSource=false;
  if(Mode=="product_count")result.Products.Candidates=new GingerCatalogueCandidate[0];
  if(Mode=="product_title")result.Products.Candidates[0].Title="Product C";
  if(Mode=="product_search")result.Products.Candidates[0].DescriptionText="Other description";
  if(Mode=="product_position")result.Products.Candidates[0].ObservedPosition=2;
  if(Mode=="product_selected")result.Products.Candidates[0].Selected=true;
  if(Mode=="product_checked")result.Products.Candidates[0].Checked=true;
  if(Mode=="product_state")result.Products.Candidates[0].StateImage=2;
  if(Mode=="product_bounds")result.Products.Candidates[0].Bounds[0]=20;
  if(Mode=="product_action")result.Products.Candidates[0].DefaultAction="other";
  if(Mode=="product_panel")result.Products.PanelHandle=21;
  if(Mode=="product_source")result.Products.Source.Title="Other source";
  return result;
 }
 public static GingerFilterOnlyContext ReadFilterOnlyContext(IntPtr window,uint owner,int pause) {
  FilterReads++;Events.Add("overflow");ReaderGuard(window,owner,pause);
  if(Mode=="became_bounded")throw new InvalidOperationException("Candidate list is within the read limit; complete observation required");
  if(Mode=="lexical")throw new InvalidOperationException("Search words do not describe selected source");
  var result=ExpectedFilter();ChangeWords(result.SearchWords);
  if(Mode=="product_count")result.ProductRows=279;
  if(Mode=="product_panel")result.ProductPanelHandle=21;
  return result;
 }
 public static void SetSearchWord(IntPtr window,uint owner,GingerCandidateSnapshot words,string word,bool enabled,bool confirmed) {
  if(window!=new IntPtr(7) || owner!=8 || word!="ALDHEYX" || !enabled || !confirmed || words.Candidates.Length!=2)
   throw new Exception("Filter binding changed");
  Inputs++;Events.Add("input");
  if(Mode=="filter_error")throw new InvalidOperationException("Filter outcome not verified");
 }
'@ + ($methods -join "`n") + "`n}"
if($code -match 'DllImport|IAccessible|Get-Process|SendInput'){throw 'Synthetic comparison code includes native access'}
Add-Type $code
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/windows/ginger-desktop-step.ps1') -Raw -Encoding UTF8
$start=$source.IndexOf('$searchPath=Join-Path $ExpectedDirectory')
$callEnd=$source.IndexOf('[void][GingerMenuAccess]::SetSearchWord(', $start)
$verifiedEnd=$source.IndexOf("Write-ReadStage 'filter_word_verified'", $callEnd)
$end=$source.IndexOf("`n",$verifiedEnd)
if($start -lt 0 -or $callEnd -le $start -or $verifiedEnd -le $callEnd -or $end -le $verifiedEnd){throw 'Production pre-filter boundaries missing'}
$fragmentText=$source.Substring($start,$end-$start)
if($fragmentText -match 'Get-Process|Add-Type|ActivateForRead|Start-Process|Invoke-Expression|RestoreProductSelection'){
 throw 'Production test fragment unexpectedly includes desktop setup/navigation'
}
$fragment=[scriptblock]::Create($fragmentText)
$validationLine=($source -split "`r?`n" | Where-Object { $_.StartsWith('if($RequireFreshFilterContext -and $Operation') })
if(@($validationLine).Count -ne 1 -or $source.IndexOf($validationLine) -gt $source.IndexOf('$source=[IO.File]')){
 throw 'Fresh-filter flag must be rejected for other operations before driver import'
}
$validation=[scriptblock]::Create($validationLine)
function Require([bool]$Condition,[string]$Message) {if(-not $Condition){throw $Message}}
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$fixtureRoot=Join-Path $temporaryRoot ('babyai-ginger-fresh-filter-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$caseNumber=0
function Invoke-FilterFixture {
 param([string]$Mode='same',[bool]$FilterOnly=$false,[bool]$RequireFreshFilterContext=$true,
  [bool]$ShouldFail=$false,[bool]$Ambiguous=$false,[bool]$Missing=$false)
 [GingerMenuAccess]::Reset($Mode)
 $script:caseNumber++
 $ExpectedDirectory=Join-Path $fixtureRoot $script:caseNumber
 [void][IO.Directory]::CreateDirectory($ExpectedDirectory)
 $expected=if($FilterOnly){[GingerMenuAccess]::ExpectedFilter()}else{[GingerMenuAccess]::ExpectedFull()}
 if($Mode -eq 'expected_stale'){$expected.QueryWordsMatchSource=$false}
 $name=if($FilterOnly){'filter-context.json'}else{'search-context.json'}
 if(-not $Missing){$expected | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $ExpectedDirectory $name) -Encoding UTF8}
 if($Ambiguous){[GingerMenuAccess]::ExpectedFilter() | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $ExpectedDirectory 'filter-context.json') -Encoding UTF8}
 $window=[IntPtr]7;$ownerId=[uint32]8;$Word='ALDHEYX';$Enabled=$true
 $stages=New-Object 'System.Collections.Generic.List[string]'
 function Write-ReadStage([string]$Stage) {
  Require ($Stage -in @('filter_context_recheck','filter_context_verified','filter_word_start','filter_word_verified')) 'Unexpected filter stage'
  $stages.Add($Stage)
 }
 $failure=$null
 try {& $fragment} catch {$failure=$_}
 Require (($null -ne $failure) -eq $ShouldFail) "Unexpected $Mode outcome: $failure"
 Require ([GingerMenuAccess]::Inputs -eq [int]((-not $ShouldFail) -or $Mode -eq 'filter_error')) "$Mode sent input before failed validation or repeated input"
 Require (($stages -contains 'filter_word_verified') -eq (-not $ShouldFail)) "$Mode reported an unverified filter as complete"
 Require ([GingerMenuAccess]::FullReads -le 1 -and [GingerMenuAccess]::FilterReads -le 1) "$Mode retried a pre-input read"
 Require ([GingerMenuAccess]::FullReads+[GingerMenuAccess]::FilterReads -le 1) "$Mode switched context types before input"
 if($RequireFreshFilterContext -and -not $ShouldFail) {
  Require (($stages -join ',') -eq 'filter_context_recheck,filter_context_verified,filter_word_start,filter_word_verified') 'Fresh context or filter verification stage missing'
  $reader=if($FilterOnly){'overflow'}else{'full'}
  Require (([GingerMenuAccess]::Events -join ',') -eq ($reader+',input')) 'Input preceded fresh context verification'
 }
 if(-not $RequireFreshFilterContext){Require ([GingerMenuAccess]::FullReads+[GingerMenuAccess]::FilterReads -eq 0) 'Legacy filter gained extra context I/O'}
}
try {
 foreach($operationValue in @('Observe','Prepare','Select','Filter')) {
  $Operation=$operationValue;$RequireFreshFilterContext=$true;$rejected=$false
  try {& $validation} catch {$rejected=$true}
  Require ($rejected -eq ($Operation -ne 'Filter')) 'Fresh-filter flag accepted for a different operation'
  $RequireFreshFilterContext=$false
  & $validation
 }
 foreach($filterOnly in @($false,$true)) {
  Invoke-FilterFixture -FilterOnly $filterOnly
  Invoke-FilterFixture -FilterOnly $filterOnly -RequireFreshFilterContext $false -Mode product_count
  Invoke-FilterFixture -FilterOnly $filterOnly -Mode filter_error -ShouldFail $true
  foreach($mode in @('product_count','product_panel','word_title','word_search','word_checked','word_panel',
    'source_table','source_position','source_document','source_supplier','source_title','source_price','source_linked',
    'lexical','expected_stale','read_error')) {
   Invoke-FilterFixture -FilterOnly $filterOnly -Mode $mode -ShouldFail $true
  }
 }
 foreach($mode in @('product_title','product_search','product_position','product_selected','product_checked',
   'product_state','product_bounds','product_action','product_source','became_overflow')) {
  Invoke-FilterFixture -Mode $mode -ShouldFail $true
 }
 Invoke-FilterFixture -Mode empty
 Invoke-FilterFixture -FilterOnly $true -Mode became_bounded -ShouldFail $true
 Invoke-FilterFixture -Ambiguous $true -ShouldFail $true
 Invoke-FilterFixture -Missing $true -ShouldFail $true
 "PASS: $script:caseNumber production fresh-filter cases plus flag scope; exact full lists or overflow metadata, no input on conflicts, no real UI"
} finally {
 $cleanupPath=[IO.Path]::GetFullPath($fixtureRoot)
 $cleanupPrefix=$temporaryRoot+[IO.Path]::DirectorySeparatorChar+'babyai-ginger-fresh-filter-'
 if(-not $cleanupPath.StartsWith($cleanupPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup path'}
 if(Test-Path -LiteralPath $cleanupPath) {
  if((Get-Item -LiteralPath $cleanupPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected fixture reparse point'}
  Remove-Item -LiteralPath $cleanupPath -Recurse -Force
 }
}
