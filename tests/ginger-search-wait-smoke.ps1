# Pure observation-loop test: no desktop access or input.
$ErrorActionPreference='Stop'
Add-Type @"
using System;
public class Context { public bool QueryWordsMatchSource; }
public class GingerUiBusyException : Exception { }
public class GingerCandidateLimitException : Exception { }
public static class GingerMenuAccess {
 public static int BusyReads;
 public static int Reads; public static bool ChangeRows; public static bool NeverReady; public static bool ChangeCandidates;
 public static Context ReadSearchContext(IntPtr w,uint p,int pause) {
  if(pause!=700) throw new Exception("Settling pause removed");
  Reads++; System.Threading.Thread.Sleep(pause);
  if(BusyReads>0) { BusyReads--; throw new GingerUiBusyException(); }
  if(ChangeCandidates) throw new Exception("Candidates changed");
  return new Context { QueryWordsMatchSource=!NeverReady && Reads>1 };
 }
 public static Context ReadFilterOnlyContext(IntPtr w,uint p,int pause) { throw new Exception("Unexpected filter-only fallback"); }
 public static object ReadProductRows(IntPtr w,uint p) { return null; }
 public static void RequireSameProductRows(object a,object b,bool selected) { if(!selected) throw new Exception("Selection guard disabled"); if(ChangeRows) throw new Exception("Rows changed"); }
 public static void RequireSameSearchContext(Context a,Context b) { if(ChangeCandidates) throw new Exception("Candidates changed"); if(!a.QueryWordsMatchSource || !b.QueryWordsMatchSource) throw new Exception("Stale accepted"); }
}
"@
$source=Get-Content (Join-Path $PSScriptRoot '../scripts/windows/ginger-desktop-step.ps1') -Raw
$start=$source.IndexOf('$searchDeadline=')
if($start -lt 0){throw 'Production search loop start not found'}
$end=$source.IndexOf('[pscustomobject]@{process_id=', $start)
if($end -le $start){throw 'Production search loop end not found'}
$loop=[scriptblock]::Create($source.Substring($start,$end-$start))
$window=[IntPtr]::Zero; $ownerId=[uint32]0; $rows=$null
function Write-ReadStage([string]$Stage) {
 if($Stage -notin @('search_pair','table_recheck','verified','search_busy')){throw 'Unexpected read stage'}
}
& $loop
if([GingerMenuAccess]::Reads -ne 2){throw 'Expected stale then fresh verified context'}
[GingerMenuAccess]::BusyReads=1
$readsBefore=[GingerMenuAccess]::Reads
& $loop
if([GingerMenuAccess]::Reads -ne ($readsBefore+2)){throw 'Transient busy observation not recovered once'}
[GingerMenuAccess]::BusyReads=3
$readsBefore=[GingerMenuAccess]::Reads
$failed=$false
try { & $loop } catch {
 if($_.Exception.GetBaseException() -isnot [GingerUiBusyException]){throw}
 $failed=$true
}
if(-not $failed -or [GingerMenuAccess]::Reads -ne ($readsBefore+2)){throw 'Repeated busy observation must stop after one retry'}
[GingerMenuAccess]::BusyReads=1
[GingerMenuAccess]::ChangeRows=$true
$failed=$false
try { & $loop } catch {
 if($_.Exception.ToString() -notmatch 'Rows changed'){throw}
 $failed=$true
}
if(-not $failed){throw 'Busy recovery accepted changed rows'}
[GingerMenuAccess]::ChangeRows=$true
$failed=$false
try { & $loop } catch {
 if($_.Exception.ToString() -notmatch 'Rows changed'){throw}
 $failed=$true
}
if(-not $failed){throw 'Changed rows accepted'}
[GingerMenuAccess]::ChangeRows=$false
[GingerMenuAccess]::ChangeCandidates=$true
$readsBefore=[GingerMenuAccess]::Reads
$failed=$false
try { & $loop } catch {
 if($_.Exception.ToString() -notmatch 'Candidates changed'){throw}
 $failed=$true
}
if(-not $failed){throw 'Changing candidates accepted'}
if([GingerMenuAccess]::Reads -ne ($readsBefore+1)){throw 'Candidate conflict retried instead of stopping'}
[GingerMenuAccess]::ChangeCandidates=$false
[GingerMenuAccess]::NeverReady=$true
$failed=$false
$elapsed=[Diagnostics.Stopwatch]::StartNew()
try { & $loop } catch {
 if($_.Exception.ToString() -notmatch 'Search query differs from source after bounded observation; no input retry'){throw}
 $failed=$true
}
if(-not $failed){throw 'Permanently stale context accepted'}
if($elapsed.Elapsed.TotalSeconds -lt 10){throw 'Stale observation stopped before its observation window'}
'PASS: delayed context, selection guard, changed source/candidate rejection, bounded stale rejection; no input'
