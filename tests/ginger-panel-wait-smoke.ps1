$ErrorActionPreference='Stop'
Add-Type @'
using System;
public class GingerUiBusyException : Exception {}
public class View { public string Status="rows_observed_identity_unverified"; }
public static class GingerMenuAccess {
 public static int Reads; public static bool Fatal;
 public static View InspectMatching(IntPtr w,uint p) {
  Reads++;
  if(Fatal) throw new InvalidOperationException("Wrong panel");
  if(Reads==1) throw new GingerUiBusyException();
  return new View();
 }
}
'@
$window=[IntPtr]::Zero; $processId=[uint32]0; $ownerId=[uint32]0
foreach($name in @('ginger-navigation.ps1','ginger-desktop-step.ps1')) {
 $source=Get-Content (Join-Path $PSScriptRoot "../scripts/windows/$name") -Raw
 if($name -eq 'ginger-navigation.ps1') {
  $start=$source.IndexOf('    $timer = [Diagnostics.Stopwatch]::StartNew()', $source.IndexOf("if (`$Mode -eq 'OpenMatching')"))
  $end=$source.IndexOf('    $matchingStatus =', $start)
 } else {
  $start=$source.IndexOf(' $deadline=[Diagnostics.Stopwatch]::StartNew()')
  $end=$source.IndexOf(' $afterDocument=', $start)
 }
 if($start -lt 0 -or $end -le $start){throw 'Loop boundaries missing'}
 $loop=[scriptblock]::Create($source.Substring($start,$end-$start))
 [GingerMenuAccess]::Reads=0; [GingerMenuAccess]::Fatal=$false
 & $loop
 if([GingerMenuAccess]::Reads -ne 2){throw 'Busy observation did not recover exactly once'}
 [GingerMenuAccess]::Reads=0; [GingerMenuAccess]::Fatal=$true
 $rejected=$false
 try { & $loop } catch {
  if($_.Exception.GetBaseException().Message -ne 'Wrong panel'){throw}
  $rejected=$true
 }
 if(-not $rejected -or [GingerMenuAccess]::Reads -ne 1){throw 'Fatal error retried or accepted'}
}
'PASS: both production panel loops recover busy observation and reject unrelated errors; no UI input'
