# Pure observation-loop test: no desktop access or input.
$ErrorActionPreference='Stop'
Add-Type @"
using System;
public class Context { public bool QueryWordsMatchSource; }
public static class GingerMenuAccess {
 public static int Reads; public static bool ChangeRows; public static bool NeverReady; public static bool ChangeCandidates;
 public static Context ReadSearchContext(IntPtr w,uint p) { return new Context { QueryWordsMatchSource=!NeverReady && ++Reads>2 }; }
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
& $loop
if([GingerMenuAccess]::Reads -ne 4){throw 'Expected stale then fresh pair'}
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
if([GingerMenuAccess]::Reads -ne ($readsBefore+2)){throw 'Candidate conflict retried instead of stopping'}
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
