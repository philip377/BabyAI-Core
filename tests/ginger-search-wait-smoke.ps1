# Execute the production observation loop only: no desktop access or input.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type @'
using System;
public class Context { public bool QueryWordsMatchSource; }
public class GingerUiBusyException : Exception { }
public class GingerCandidateLimitException : Exception { }
public class GingerSearchPendingException : InvalidOperationException {
 public GingerSearchPendingException(string message) : base(message) { }
}
public static class GingerMenuAccess {
 public static int Reads,FilterReads,Guards,ChangeRowsAtGuard,SlowFirstMilliseconds;
 public static object OriginalRows=new object();
 public static string[] Full,Filter;
 public static string FullDefault,FilterDefault;
 public static void Reset(string[] full,string[] filter,string fullDefault,string filterDefault,int conflict,int slow) {
  Reads=FilterReads=Guards=0;Full=full;Filter=filter;
  FullDefault=fullDefault;FilterDefault=filterDefault;ChangeRowsAtGuard=conflict;SlowFirstMilliseconds=slow;
 }
 private static string Next(string[] sequence,int index,string fallback) {
  return index<sequence.Length?sequence[index]:fallback;
 }
 public static Context ReadSearchContext(IntPtr w,uint p,int pause) {
  if(pause!=700)throw new Exception("Settling pause removed");
  string next=Next(Full,Reads++,FullDefault);
  System.Threading.Thread.Sleep(pause+(Reads==1?SlowFirstMilliseconds:0));
  if(next=="busy")throw new GingerUiBusyException();
  if(next=="overflow")throw new GingerCandidateLimitException();
  if(next=="changed")throw new InvalidOperationException("Candidates changed");
  if(next!="ready" && next!="stale")throw new Exception("Unknown full fixture");
  return new Context {QueryWordsMatchSource=next=="ready"};
 }
 public static Context ReadFilterOnlyContext(IntPtr w,uint p,int pause) {
  if(pause!=700)throw new Exception("Filter settling pause removed");
  string next=Next(Filter,FilterReads++,FilterDefault);
  if(next=="stale")throw new GingerSearchPendingException("Search words do not describe selected source");
  if(next=="bounded")throw new GingerSearchPendingException("Candidate list is within the read limit; complete observation required");
  if(next=="changed")throw new InvalidOperationException("Filter context changed");
  if(next!="ready")throw new Exception("Unexpected filter-only fallback");
  return new Context {QueryWordsMatchSource=true};
 }
 public static object ReadProductRows(IntPtr w,uint p) {return new object();}
 public static void RequireSameProductRows(object before,object after,bool selected) {
  if(!selected || !Object.ReferenceEquals(before,OriginalRows))throw new Exception("Original table/selection guard removed");
  Guards++;
  if(Guards==ChangeRowsAtGuard)throw new InvalidOperationException("Rows changed");
 }
}
'@
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/windows/ginger-desktop-step.ps1') -Raw
$start=$source.IndexOf('$searchDeadline=')
$end=$source.IndexOf('[pscustomobject]@{process_id=', $start)
if($start -lt 0 -or $end -le $start){throw 'Production search loop boundaries missing'}
$fragment=$source.Substring($start,$end-$start)
if($fragment -match 'RestoreProductSelection|SetSearchWord|OpenDocumentMatching|SendInput|Get-Process|Start-Process'){
 throw 'Observation loop unexpectedly contains input or process control'
}
$loop=[scriptblock]::Create($fragment)
$cases=0
function Require([bool]$Condition,[string]$Message) {if(-not $Condition){throw $Message}}
function Invoke-SearchFixture {
 param([string[]]$Full=@('ready'),[string[]]$Filter=@(),[string]$FullDefault='ready',
  [string]$FilterDefault='unexpected',[int]$ConflictGuard=0,[int]$SlowFirstMilliseconds=0,[string]$ExpectedError='')
 [GingerMenuAccess]::Reset($Full,$Filter,$FullDefault,$FilterDefault,$ConflictGuard,$SlowFirstMilliseconds)
 $window=[IntPtr]::Zero;$ownerId=[uint32]0;$rows=[GingerMenuAccess]::OriginalRows
 $stages=New-Object 'System.Collections.Generic.List[string]'
 function Write-ReadStage([string]$Stage) {
  Require ($Stage -in @('search_pair','table_recheck','verified','search_busy','search_filters_only','search_pending')) 'Unexpected read stage'
  $stages.Add($Stage)
 }
 $failed=$false;$elapsed=[Diagnostics.Stopwatch]::StartNew()
 try {& $loop} catch {
  $base=$_.Exception.GetBaseException()
  if(-not $ExpectedError -or ($base.Message -notmatch $ExpectedError -and $base.GetType().Name -notmatch $ExpectedError)){throw}
  $failed=$true
 }
 Require ($failed -eq [bool]$ExpectedError) 'Expected rejection did not occur'
 Require ($stages.Contains('verified') -eq (-not $failed)) 'An incomplete context reached verified stage'
 $script:cases++
 [pscustomobject]@{Reads=[GingerMenuAccess]::Reads;Filters=[GingerMenuAccess]::FilterReads;
  Guards=[GingerMenuAccess]::Guards;Stages=$stages.ToArray();Seconds=$elapsed.Elapsed.TotalSeconds}
}

$normal=Invoke-SearchFixture -Full @('stale','ready')
Require ($normal.Reads -eq 2 -and $normal.Guards -eq 2 -and $normal.Filters -eq 0) 'Normal stale-to-fresh observation changed'
$busy=Invoke-SearchFixture -Full @('busy','ready')
Require ($busy.Reads -eq 2 -and $busy.Guards -eq 1) 'One busy read did not recover'
$busyTwice=Invoke-SearchFixture -Full @('busy','busy','ready') -ExpectedError 'GingerUiBusyException'
Require ($busyTwice.Reads -eq 2) 'Busy retry exceeded its separate one-retry budget'
$busyConflict=Invoke-SearchFixture -Full @('busy','ready') -ConflictGuard 1 -ExpectedError 'Rows changed'
Require ($busyConflict.Reads -eq 2 -and $busyConflict.Guards -eq 1) 'Busy recovery accepted a changed table'

foreach($pending in @('stale','bounded')) {
 $ready=Invoke-SearchFixture -Full @('overflow','ready') -Filter @($pending)
 Require ($ready.Reads -eq 2 -and $ready.Filters -eq 1 -and $ready.Guards -eq 2) "$pending transition did not recheck original table and return to full reading"
 Require (@($ready.Stages | Where-Object {$_ -eq 'search_pending'}).Count -eq 1) "$pending transition was not reported exactly once"
 $guarded=Invoke-SearchFixture -Full @('overflow','ready') -Filter @($pending) -ConflictGuard 1 -ExpectedError 'Rows changed'
 Require ($guarded.Reads -eq 1 -and $guarded.Filters -eq 1 -and $guarded.Guards -eq 1) "$pending transition retried after the original table changed"
 $lateConflict=Invoke-SearchFixture -Full @('overflow','ready') -Filter @($pending) -ConflictGuard 2 -ExpectedError 'Rows changed'
 Require ($lateConflict.Reads -eq 2 -and $lateConflict.Guards -eq 2) "$pending retry skipped final table verification"
 $never=Invoke-SearchFixture -Full @() -Filter @() -FullDefault 'overflow' -FilterDefault $pending -ExpectedError 'Ginger search did not settle after one additional observation; no input retry'
 Require ($never.Reads -eq 2 -and $never.Filters -eq 2 -and $never.Guards -ge 1 -and $never.Guards -le 2) "$pending observation exceeded one additional read"
}
$wideReady=Invoke-SearchFixture -Full @('overflow','overflow') -Filter @('stale','ready')
Require ($wideReady.Reads -eq 2 -and $wideReady.Filters -eq 2 -and $wideReady.Guards -eq 2) 'A settled overflow context could not be observed'
$mixed=Invoke-SearchFixture -Full @('busy','overflow','ready') -Filter @('bounded')
Require ($mixed.Reads -eq 3 -and $mixed.Filters -eq 1 -and $mixed.Guards -eq 2) 'Busy and pending budgets interfered'
$mixedBusy=Invoke-SearchFixture -Full @('busy','overflow','busy','ready') -Filter @('stale') -ExpectedError 'GingerUiBusyException'
Require ($mixedBusy.Reads -eq 3 -and $mixedBusy.Filters -eq 1) 'Pending reset the busy retry budget'
$mixedPending=Invoke-SearchFixture -Full @('overflow','busy','overflow','ready') -Filter @('stale','bounded') -ExpectedError 'Ginger search did not settle after one additional observation; no input retry'
Require ($mixedPending.Reads -eq 3 -and $mixedPending.Filters -eq 2) 'Busy reset the pending retry budget'
$changed=Invoke-SearchFixture -Full @('changed','ready') -ExpectedError 'Candidates changed'
Require ($changed.Reads -eq 1 -and $changed.Guards -eq 0) 'Generic candidate conflict retried'
$changedFilter=Invoke-SearchFixture -Full @('overflow','ready') -Filter @('changed') -ExpectedError 'Filter context changed'
Require ($changedFilter.Reads -eq 1 -and $changedFilter.Filters -eq 1 -and $changedFilter.Guards -eq 0) 'Generic filter conflict retried'

$slow=Invoke-SearchFixture -Full @('overflow','ready') -Filter @('bounded') -SlowFirstMilliseconds 10100
Require ($slow.Seconds -ge 10 -and $slow.Reads -eq 2 -and $slow.Guards -eq 2) 'First expensive observation consumed the pending retry before it could read fresh data'
$never=Invoke-SearchFixture -Full @() -FullDefault 'stale' -ExpectedError 'Search query differs from source after bounded observation; no input retry'
Require ($never.Seconds -ge 10 -and $never.Seconds -lt 20 -and $never.Reads -eq $never.Guards) 'Ordinary stale observation lost its bounded deadline or guard'
"PASS: $cases production search-loop cases; slow/pending overflow transitions, hard conflicts and separate one-read budgets; no real UI or input"
