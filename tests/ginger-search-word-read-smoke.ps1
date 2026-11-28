# Compile production read/input orchestration against synthetic readers and input.
# No driver import, Accessibility assembly, native API, process lookup or real window.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/windows/ginger-navigation.ps1') -Raw -Encoding UTF8
function Definition([string]$Signature) {
 $start=$source.IndexOf($Signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Production definition missing: $Signature"}
 $opening=$source.IndexOf('{',$start);$depth=0
 for($index=$opening;$index -lt $source.Length;$index++) {
  if($source[$index] -eq '{'){$depth++}
  elseif($source[$index] -eq '}') {
   $depth--
   if($depth -eq 0){return $source.Substring($start,$index-$start+1)}
  }
 }
 throw "Unclosed production definition: $Signature"
}
$classes=@('public sealed class GingerUiBusyException',
 'public sealed class GingerSelectedProduct','public sealed class GingerCandidateSnapshot') |
 ForEach-Object {Definition $_}
$methods=@('public static void RequireSameSelectedProduct(', 'private static bool SameBounds(',
 'public static void RequireSameCandidates(', 'public static GingerCandidateSnapshot ReadSearchWords(',
 'public static bool SearchWordsDescribeSource(', 'private static bool SearchWordState(',
 'public static void VerifySearchWordState(', 'public static GingerCandidateSnapshot SetSearchWord(') |
 ForEach-Object {Definition $_}
# Keep the real source-before/source-after guards. Only the panel-access body is fake.
$reader=Definition 'private static GingerCandidateSnapshot ReadCandidatesOnce('
$bodyStart=$reader.IndexOf('foreach(IntPtr hwnd in Windows(root,process))')
$bodyEnd=$reader.IndexOf('if(result==null) throw', $bodyStart)
if($bodyStart -lt 0 -or $bodyEnd -le $bodyStart){throw 'Production candidate reader boundaries missing'}
$reader=$reader.Substring(0,$bodyStart) + 'result=ReadSyntheticPanel(source,adjacentSource,searchWords);' + "`n" + $reader.Substring($bodyEnd)
$code=@'
using System;
using System.Collections.Generic;
using System.Text;
public sealed class GingerCatalogueCandidate {
 public int ObservedPosition,StateImage;
 public string Title,DescriptionText,DefaultAction;
 public bool Selected;
 public bool? Checked;
 public int[] Bounds;
}
// Deliberately local substitutes: production declarations/PInvoke are not included.
public interface IAccessible {
 object accHitTest(int x,int y);
 string get_accName(object id);
 void accLocation(out int x,out int y,out int width,out int height,object id);
}
public struct Point {public int X,Y;}
public struct MouseInput {public int X,Y;public uint Flags;}
public struct Input {public MouseInput Mouse;}
public static class Marshal {public static int SizeOf(Type value){return 32;}}
'@ + ($classes -join "`n") + @'

public static class GingerMenuAccess {
 public static int SourceReads,PanelReads,Inputs,ForegroundChecks,PointChecks,Checks;
 private static int changeSourceAt,changePanelAt,throwSourceAt,throwPanelAt,epoch,lastEndEpoch;
 private static string sourceField,panelField,fault;
 private static GingerSelectedProduct lastEnd;
 private static bool enabled;
 private static readonly IntPtr Root=new IntPtr(1);
 public static readonly List<string> Events=new List<string>();
 private static void Reset() {
  SourceReads=PanelReads=Inputs=ForegroundChecks=PointChecks=0;
  changeSourceAt=changePanelAt=throwSourceAt=throwPanelAt=epoch=lastEndEpoch=0;
  sourceField=panelField=fault="";lastEnd=null;enabled=false;Events.Clear();
 }
 private static GingerSelectedProduct NewSource() {
  return new GingerSelectedProduct {TableHandle=100,ObservedRowPosition=9,DocumentNumber="price",
   Supplier="supplier",Title="Alpha Beta",PriceText="100",LinkedProductText="none"};
 }
 private static GingerSelectedProduct ReadSelectedProductOnce(IntPtr root,uint process) {
  SourceReads++;Events.Add("source"+SourceReads);
  if(SourceReads==throwSourceAt)throw new InvalidOperationException("Synthetic source read failed");
  var value=NewSource();
  if(SourceReads==changeSourceAt) {
   switch(sourceField) {
    case "table":value.TableHandle++;break;
    case "position":value.ObservedRowPosition++;break;
    case "document":value.DocumentNumber="other";break;
    case "supplier":value.Supplier="other";break;
    case "title":value.Title="Other Beta";break;
    case "price":value.PriceText="101";break;
    case "linked":value.LinkedProductText="linked";break;
   }
  }
  lastEnd=value;lastEndEpoch=epoch;
  return value;
 }
 private static GingerSelectedProduct ReadSelectedProduct(IntPtr root,uint process) {
  throw new InvalidOperationException("Redundant double source read reached");
 }
 private static GingerCatalogueCandidate Row(string title,int position,bool on) {
  return new GingerCatalogueCandidate {Title=title,DescriptionText=title,ObservedPosition=position,
   DefaultAction="toggle",Checked=on,StateImage=on?2:0,Bounds=new[]{10,20,100,20}};
 }
 private static GingerCandidateSnapshot ReadSyntheticPanel(GingerSelectedProduct source,GingerSelectedProduct adjacent,bool words) {
  if(!words)throw new InvalidOperationException("Unexpected product reader");
  if(adjacent!=null && (!Object.ReferenceEquals(adjacent,lastEnd) || lastEndEpoch!=epoch))
   throw new InvalidOperationException("Source reused across input or an external operation");
  PanelReads++;Events.Add("panel"+PanelReads);
  if(PanelReads==throwPanelAt)throw new InvalidOperationException("Synthetic panel read failed");
  var rows=new[]{Row("Alpha",1,enabled),Row("Beta",2,false)};
  var result=new GingerCandidateSnapshot {Source=source,PanelHandle=10,Candidates=rows};
  if(PanelReads==changePanelAt) {
   switch(panelField) {
    case "panel":result.PanelHandle++;break;
    case "count":result.Candidates=new[]{rows[0]};break;
    case "title":rows[0].Title="Other";break;
    case "description":rows[0].DescriptionText="other";break;
    case "position":rows[0].ObservedPosition++;break;
    case "selected":rows[0].Selected=true;break;
    case "checked":rows[0].Checked=!enabled;break;
    case "state":rows[0].StateImage=3;break;
    case "action":rows[0].DefaultAction="other";break;
    case "bounds":rows[0].Bounds[0]++;break;
   }
  }
  return result;
 }
 private static void RequireWindow(IntPtr root,uint process) {
  if(root!=Root || process!=2)throw new InvalidOperationException("Wrong window/process");
  if(fault=="window")throw new InvalidOperationException("Synthetic window changed");
 }
 private static void External(){epoch++;}
 private static void RequireForeground(IntPtr root,uint process) {
  External();RequireWindow(root,process);ForegroundChecks++;
  if(fault=="foreground" && ForegroundChecks==2)throw new InvalidOperationException("Foreground changed");
 }
 private static void RequireInputScale(IntPtr root){External();if(fault=="scale")throw new InvalidOperationException("Scale changed");}
 private static IEnumerable<IntPtr> Windows(IntPtr root,uint process){External();return new IntPtr[0];}
 private static int GetClassNameW(IntPtr hwnd,StringBuilder cls,int capacity){throw new Exception("Unexpected child");}
 private static bool IsWindowVisible(IntPtr hwnd){throw new Exception("Unexpected child");}
 private sealed class FakeList:IAccessible {
  public object accHitTest(int x,int y){External();return 1;}
  public string get_accName(object id){External();return fault=="hit"?"Other":"Alpha";}
  public void accLocation(out int x,out int y,out int width,out int height,object id) {
   External();x=fault=="geometry"?11:10;y=20;width=100;height=20;
  }
 }
 private static IAccessible Client(IntPtr hwnd){External();return new FakeList();}
 private static IntPtr WindowFromPoint(Point point) {
  External();PointChecks++;return new IntPtr(fault=="covered" && PointChecks==2?11:10);
 }
 private static int GetSystemMetrics(int index){External();return index==78?1920:index==79?1080:0;}
 private static uint SendInput(uint count,Input[] input,int size) {
  External();Inputs++;Events.Add("input");
  Check(count==2 && input.Length==2 && input[0].Mouse.Flags==0xC003 && input[1].Mouse.Flags==0xC005,
   "Unexpected synthetic input sequence");
  if(fault=="partial-input")return 1;
  enabled=true;return 2;
 }
 private static void Check(bool condition,string message){Checks++;if(!condition)throw new Exception(message);}
 private static void Reject(Action action,string label,int expectedInputs) {
  bool failed=false;
  try{action();}catch(InvalidOperationException){failed=true;}
  Check(failed,"Accepted changed/failed observation: "+label);
  Check(Inputs==expectedInputs,"Input repeated or sent before failed guard: "+label);
 }
 private static GingerCandidateSnapshot Expected() {
  return new GingerCandidateSnapshot {Source=NewSource(),PanelHandle=10,
   Candidates=new[]{Row("Alpha",1,enabled),Row("Beta",2,false)}};
 }
 public static int Run() {
  Checks=0;Reset();
  var words=ReadSearchWords(Root,2);
  Check(SourceReads==3 && PanelReads==2,"Adjacent words read repeated the full source scan");
  Check(String.Join(",",Events)=="source1,panel1,source2,panel2,source3","Source did not bracket both complete panel reads");
  Check(Object.ReferenceEquals(words.Source,lastEnd),"Returned the old source instead of the verified end source");
  Check(!words.CanMatch && !words.IdentityVerified && !words.SearchCompletionVerified,"Read gained match authority");
  foreach(string field in new[]{"table","position","document","supplier","title","price","linked"})
   for(int at=1;at<=3;at++) {
    Reset();sourceField=field;changeSourceAt=at;
    Reject(delegate{ReadSearchWords(Root,2);},"source "+field+" at "+at,0);
   }
  foreach(string field in new[]{"panel","count","title","description","position","selected","checked","state","action","bounds"}) {
   Reset();panelField=field;changePanelAt=2;
   Reject(delegate{ReadSearchWords(Root,2);},"panel "+field,0);
  }
  for(int at=1;at<=3;at++) {
   Reset();throwSourceAt=at;Reject(delegate{ReadSearchWords(Root,2);},"source exception "+at,0);
   Check(SourceReads==at,"Source exception was retried");
  }
  for(int at=1;at<=2;at++) {
   Reset();throwPanelAt=at;Reject(delegate{ReadSearchWords(Root,2);},"panel exception "+at,0);
   Check(PanelReads==at,"Panel exception was retried");
  }
  Reset();SetSearchWord(Root,2,Expected(),"Alpha",true,true);
  Check(Inputs==1 && SourceReads==9 && PanelReads==6,"Filter did not keep three fresh double panel reads and one input");
  Check(ForegroundChecks==2 && PointChecks==2,"Final foreground/covered-target guards were omitted");
  Check(String.Join(",",Events)=="source1,panel1,source2,panel2,source3,source4,panel3,source5,panel4,source6,input,source7,panel5,source8,panel6,source9",
   "Source reused across hit-test/input or post-input verification removed");
  Reset();enabled=true;SetSearchWord(Root,2,Expected(),"Alpha",true,true);
  Check(Inputs==0 && PanelReads==2 && SourceReads==3,"Already enabled filter sent input");
  for(int at=1;at<=9;at++) {
   Reset();sourceField="position";changeSourceAt=at;
   Reject(delegate{SetSearchWord(Root,2,Expected(),"Alpha",true,true);},"filter source "+at,at>=7?1:0);
  }
  foreach(int at in new[]{3,4,5,6}) {
   Reset();panelField="description";changePanelAt=at;
   Reject(delegate{SetSearchWord(Root,2,Expected(),"Alpha",true,true);},"filter panel "+at,at>=5?1:0);
   Reset();throwPanelAt=at;
   Reject(delegate{SetSearchWord(Root,2,Expected(),"Alpha",true,true);},"filter panel exception "+at,at>=5?1:0);
   Check(PanelReads==at,"Filter exception was retried");
  }
  foreach(string value in new[]{"window","scale","hit","geometry","foreground","covered","partial-input"}) {
   Reset();fault=value;
   Reject(delegate{SetSearchWord(Root,2,Expected(),"Alpha",true,true);},value,value=="partial-input"?1:0);
  }
  Reset();Reject(delegate{SetSearchWord(Root,2,Expected(),"Alpha",true,false);},"confirmation",0);
  Check(PanelReads==0 && SourceReads==0,"Unconfirmed input read the UI");
  Reset();Reject(delegate{ReadSearchWords(new IntPtr(3),2);},"window ownership",0);
  return Checks;
 }
'@ + $reader + "`n" + ($methods -join "`n") + "`n}"
if($code -match 'DllImport|System\.Runtime\.InteropServices|AccessibleObjectFromWindow|Get-Process|Start-Process') {
 throw 'Synthetic test unexpectedly includes real native/process access'
}
Add-Type -TypeDefinition $code
$checks=[GingerMenuAccess]::Run()
"PASS: $checks search-word read checks; production orchestration, synthetic panels/input, no real UI/native calls"
