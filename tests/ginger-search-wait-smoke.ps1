# Pure observation-loop test: no desktop access or input.
$ErrorActionPreference='Stop'
Add-Type @"
using System;
public class Context { public bool QueryWordsMatchSource; }
public static class GingerMenuAccess {
 public static int Reads; public static bool ChangeRows; public static bool NeverReady;
 public static Context ReadSearchContext(IntPtr w,uint p) { return new Context { QueryWordsMatchSource=!NeverReady && ++Reads>2 }; }
 public static object ReadProductRows(IntPtr w,uint p) { return null; }
 public static void RequireSameProductRows(object a,object b,bool selected) { if(ChangeRows) throw new Exception("Rows changed"); }
 public static void RequireSameSearchContext(Context a,Context b) { if(!a.QueryWordsMatchSource || !b.QueryWordsMatchSource) throw new Exception("Stale accepted"); }
}
"@
$source=Get-Content (Join-Path $PSScriptRoot '../scripts/windows/ginger-desktop-step.ps1') -Raw
$start=$source.IndexOf('$searchDeadline=')
$end=$source.IndexOf('[pscustomobject]@{process_id=', $start)
$loop=[scriptblock]::Create($source.Substring($start,$end-$start))
$window=[IntPtr]::Zero; $ownerId=[uint32]0; $rows=$null
& $loop
if([GingerMenuAccess]::Reads -ne 4){throw 'Expected stale then fresh pair'}
[GingerMenuAccess]::ChangeRows=$true
$failed=$false
try { & $loop } catch { $failed=$true }
if(-not $failed){throw 'Changed rows accepted'}
[GingerMenuAccess]::ChangeRows=$false
[GingerMenuAccess]::NeverReady=$true
$failed=$false
try { & $loop } catch { $failed=$true }
if(-not $failed){throw 'Permanently stale context accepted'}
'PASS: delayed context, changed source rejection, bounded stale rejection; no input'
