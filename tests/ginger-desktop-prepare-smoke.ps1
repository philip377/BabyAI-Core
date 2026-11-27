# Execute the production Prepare/input/read fragment against synthetic snapshots only.
# The live process lookup, driver import and activation are deliberately excluded.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Collections.Generic;
public class GingerUiBusyException : Exception { }
public class GingerProductRow {
 public int Snapshot { get; set; }
 public long Table { get; set; }
 public bool Selected { get; set; }
}
public class GingerCandidateSnapshot { public string Marker { get; set; } }
public class PrepareDocument { public string Number="document"; public string Supplier="supplier"; }
public class PrepareView { public string Status="rows_observed_identity_unverified"; }
public class PrepareSearchContext { public bool QueryWordsMatchSource=true; }
public static class GingerMenuAccess {
 public static int RowReads, DocumentReads, SearchReads, OpenInputs, SelectInputs, FilterInputs;
 public static int FirstSearchAtRowRead, ConflictRead, SelectionConflictRead;
 public static bool ChangeDocument;
 public static List<string> Guards=new List<string>();
 public static void Reset() {
  RowReads=DocumentReads=SearchReads=OpenInputs=SelectInputs=FilterInputs=FirstSearchAtRowRead=0;
  ConflictRead=SelectionConflictRead=0; ChangeDocument=false; Guards.Clear();
 }
 public static GingerProductRow[] ReadProductRows(IntPtr window,uint owner) {
  RowReads++;
  return new[] {new GingerProductRow {Snapshot=RowReads, Table=RowReads==ConflictRead?99:42,
   Selected=RowReads!=SelectionConflictRead}};
 }
 public static PrepareDocument ReadPriceDocument(IntPtr window,uint owner) {
  DocumentReads++;
  return new PrepareDocument {Number=ChangeDocument && DocumentReads>1?"changed":"document"};
 }
 public static void OpenDocumentMatching(IntPtr window,uint owner,PrepareDocument document,bool confirmed) {
  if(!confirmed) throw new Exception("Unconfirmed preparation");
  OpenInputs++;
 }
 public static PrepareView InspectMatching(IntPtr window,uint owner) { return new PrepareView(); }
 public static void RequireSameProductRows(GingerProductRow[] before,GingerProductRow[] after,bool selected) {
  if(!selected) throw new Exception("Selection guard disabled");
  if(before.Length!=1 || after.Length!=1) throw new Exception("Unexpected row count");
  Guards.Add(before[0].Snapshot+":"+after[0].Snapshot);
  if(before[0].Table!=after[0].Table) throw new Exception("Table changed");
  if(before[0].Selected!=after[0].Selected) throw new Exception("Selection changed");
 }
 public static void RestoreProductSelection(IntPtr window,uint owner,GingerProductRow[] rows,int position,bool confirmed) {
  if(!confirmed || position!=2 || rows[0].Snapshot!=1) throw new Exception("Wrong Select input");
  SelectInputs++;
 }
 public static void SetSearchWord(IntPtr window,uint owner,GingerCandidateSnapshot words,string word,bool enabled,bool confirmed) {
  if(!confirmed || !enabled || word!="needle" || words.Marker!="expected") throw new Exception("Wrong Filter input");
  FilterInputs++;
 }
 public static PrepareSearchContext ReadSearchContext(IntPtr window,uint owner,int pause) {
  if(pause!=700) throw new Exception("Settling pause removed");
  SearchReads++;
  if(SearchReads==1) FirstSearchAtRowRead=RowReads;
  return new PrepareSearchContext();
 }
}
'@
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/windows/ginger-desktop-step.ps1') -Raw
$start=$source.IndexOf('$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)')
$end=$source.IndexOf('[pscustomobject]@{status=''observed''', $start)
if($start -lt 0 -or $end -le $start){throw 'Production Prepare/read boundaries missing'}
$fragmentText=$source.Substring($start,$end-$start)
if($fragmentText -match 'Get-Process|Add-Type|ActivateForRead|Start-Process|Invoke-Expression'){
 throw 'Production test fragment unexpectedly includes process or desktop setup'
}
$fragment=[scriptblock]::Create($fragmentText)
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$fixtureRoot=Join-Path $temporaryRoot ('babyai-ginger-prepare-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$caseNumber=0

function Require([bool]$Condition,[string]$Message) { if(-not $Condition){throw $Message} }
function Invoke-PrepareFixture {
 param([string]$Operation,[int]$ConflictRead=0,[int]$SelectionConflictRead=0,
  [bool]$ChangeDocument=$false,[bool]$ConfirmNavigation=$true,
  [int]$ExpectedProcess=7,[long]$ExpectedTable=42,[string]$ExpectedFailure='')
 [GingerMenuAccess]::Reset()
 [GingerMenuAccess]::ConflictRead=$ConflictRead
 [GingerMenuAccess]::SelectionConflictRead=$SelectionConflictRead
 [GingerMenuAccess]::ChangeDocument=$ChangeDocument
 $script:caseNumber++
 $caseRoot=Join-Path $fixtureRoot $script:caseNumber
 $OutputDirectory=Join-Path $caseRoot 'output'
 $ExpectedDirectory=Join-Path $caseRoot 'expected'
 [void][IO.Directory]::CreateDirectory($OutputDirectory)
 [void][IO.Directory]::CreateDirectory($ExpectedDirectory)
 [pscustomobject]@{process_id=$ExpectedProcess;rows=@(@{Snapshot=0;Table=$ExpectedTable;Selected=$true})} |
  ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ExpectedDirectory 'before.json') -Encoding UTF8
 [pscustomobject]@{SearchWords=@{Marker='expected'}} |
  ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ExpectedDirectory 'search-context.json') -Encoding UTF8
 $window=[IntPtr]::Zero; $ownerId=[uint32]7; $Position=2; $Word='needle'; $Enabled=$true
 $stages=New-Object 'System.Collections.Generic.List[string]'
 function Write-ReadStage([string]$Stage) {
  Require ($Stage -in @('prepared_table','table_after_input','search_pair','table_recheck','verified','search_busy')) 'Unexpected read stage'
  $stages.Add($Stage)
 }
 $failed=$false
 try { & $fragment } catch {
  if(-not $ExpectedFailure -or $_.Exception.ToString() -notmatch $ExpectedFailure){throw}
  $failed=$true
 }
 Require ($failed -eq [bool]$ExpectedFailure) 'Expected rejection did not occur'
 $beforePath=Join-Path $OutputDirectory 'before.json'
 $saved=$null
 if($failed) {
  Require (-not (Test-Path -LiteralPath $beforePath)) 'Conflict produced an accepted snapshot'
  Require (-not $stages.Contains('verified')) 'Conflict reached verified stage'
 } else {
  $saved=Get-Content -LiteralPath $beforePath -Raw -Encoding UTF8 | ConvertFrom-Json
  Require ($saved.process_id -eq $ownerId) 'Saved snapshot process changed'
  Require ($stages.Contains('verified')) 'Final verification did not run'
 }
 [pscustomobject]@{Saved=$saved;Stages=$stages.ToArray();Rows=[GingerMenuAccess]::RowReads;
  Documents=[GingerMenuAccess]::DocumentReads;Searches=[GingerMenuAccess]::SearchReads;
  SearchAtRow=[GingerMenuAccess]::FirstSearchAtRowRead;Opens=[GingerMenuAccess]::OpenInputs;
  Selects=[GingerMenuAccess]::SelectInputs;Filters=[GingerMenuAccess]::FilterInputs;
  Guards=[GingerMenuAccess]::Guards.ToArray()}
}

try {
 $prepared=Invoke-PrepareFixture -Operation Prepare
 Require ($prepared.Rows -eq 3 -and $prepared.SearchAtRow -eq 2) 'Prepare repeated the already verified post-navigation read'
 Require ($prepared.Saved.rows[0].Snapshot -eq 2) 'Prepare saved the initial or redundant snapshot instead of the prepared snapshot'
 Require (($prepared.Guards -join ',') -eq '1:2,2:3') 'Prepare or final source/selection comparison missing'
 Require ($prepared.Documents -eq 2 -and $prepared.Opens -eq 1 -and $prepared.Searches -eq 1) 'Prepare repeated input or skipped document verification'
 Require (($prepared.Stages -join ',') -eq 'prepared_table,search_pair,table_recheck,verified') 'Prepare stages include a redundant input read'

 foreach($operation in @('Select','Filter')) {
  $result=Invoke-PrepareFixture -Operation $operation
  Require ($result.Rows -eq 3 -and $result.SearchAtRow -eq 2 -and $result.Saved.rows[0].Snapshot -eq 2) "$operation must read fresh rows after input"
  Require (($result.Guards -join ',') -eq '0:1,2:3') "$operation removed an expected/final table guard"
  Require (($result.Stages -join ',') -eq 'table_after_input,search_pair,table_recheck,verified') "$operation omitted its post-input stage"
  Require ($result.Opens -eq 0 -and $result.Selects -eq [int]($operation -eq 'Select') -and $result.Filters -eq [int]($operation -eq 'Filter')) "$operation repeated or mixed input"
 }
 $observed=Invoke-PrepareFixture -Operation Observe
 Require ($observed.Rows -eq 2 -and $observed.SearchAtRow -eq 1 -and $observed.Saved.rows[0].Snapshot -eq 1) 'Observe must retain the original no-input snapshot'
 Require ($observed.Opens+$observed.Selects+$observed.Filters -eq 0) 'Observe performed input'

 $conflict=Invoke-PrepareFixture -Operation Prepare -ConflictRead 2 -ExpectedFailure 'Table changed'
 Require ($conflict.Rows -eq 2 -and $conflict.Searches -eq 0 -and $conflict.Opens -eq 1) 'Prepared-table conflict was retried or reached search'
 $selectionConflict=Invoke-PrepareFixture -Operation Prepare -SelectionConflictRead 2 -ExpectedFailure 'Selection changed'
 Require ($selectionConflict.Rows -eq 2 -and $selectionConflict.Searches -eq 0) 'Preparation lost the selection guard'
 $documentConflict=Invoke-PrepareFixture -Operation Prepare -ChangeDocument $true -ExpectedFailure 'Document changed during preparation'
 Require ($documentConflict.Rows -eq 1 -and $documentConflict.Searches -eq 0 -and $documentConflict.Opens -eq 1) 'Changed document reached prepared table read'
 foreach($operation in @('Prepare','Select','Filter')) {
  $finalConflict=Invoke-PrepareFixture -Operation $operation -ConflictRead 3 -ExpectedFailure 'Table changed'
  Require ($finalConflict.Rows -eq 3 -and $finalConflict.Searches -eq 1) "$operation skipped the final table conflict check"
  $unconfirmed=Invoke-PrepareFixture -Operation $operation -ConfirmNavigation $false -ExpectedFailure 'confirmation required|Confirmed observation required'
  Require ($unconfirmed.Rows -eq 1 -and $unconfirmed.Searches -eq 0 -and $unconfirmed.Opens+$unconfirmed.Selects+$unconfirmed.Filters -eq 0) "$operation acted without confirmation"
 }
 foreach($operation in @('Select','Filter')) {
  $wrongProcess=Invoke-PrepareFixture -Operation $operation -ExpectedProcess 99 -ExpectedFailure 'Process changed'
  Require ($wrongProcess.Selects+$wrongProcess.Filters -eq 0 -and $wrongProcess.Rows -eq 1) "$operation accepted another process"
  $wrongTable=Invoke-PrepareFixture -Operation $operation -ExpectedTable 99 -ExpectedFailure 'Table changed'
  Require ($wrongTable.Selects+$wrongTable.Filters -eq 0 -and $wrongTable.Rows -eq 1) "$operation acted on a changed table"
 }
 'PASS: production Prepare reuses its verified fresh snapshot; conflicts stop; Select/Filter retain post-input reads and final guards; no real UI'
} finally {
 $cleanupPath=[IO.Path]::GetFullPath($fixtureRoot)
 $cleanupPrefix=$temporaryRoot+[IO.Path]::DirectorySeparatorChar+'babyai-ginger-prepare-'
 if(-not $cleanupPath.StartsWith($cleanupPrefix,[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe fixture cleanup path'}
 if(Test-Path -LiteralPath $cleanupPath) {
  if((Get-Item -LiteralPath $cleanupPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Unexpected fixture reparse point'}
  Remove-Item -LiteralPath $cleanupPath -Recurse -Force
 }
}
