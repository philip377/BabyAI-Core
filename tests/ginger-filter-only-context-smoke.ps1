# Compile only extracted pure orchestration/validation methods against synthetic
# native readers. No driver imports, windows, processes, accessibility or input.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/windows/ginger-navigation.ps1') -Raw -Encoding UTF8
function Definition([string]$Signature) {
 $start=$source.IndexOf($Signature,[StringComparison]::Ordinal)
 if($start -lt 0){throw "Production definition missing: $Signature"}
 $opening=$source.IndexOf('{',$start)
 $depth=0
 for($index=$opening;$index -lt $source.Length;$index++) {
  if($source[$index] -eq '{'){$depth++}
  elseif($source[$index] -eq '}'){
   $depth--
   if($depth -eq 0){return $source.Substring($start,$index-$start+1)}
  }
 }
 throw "Unclosed production definition: $Signature"
}
$classes=@(
 'public sealed class GingerCandidateLimitException',
 'public sealed class GingerSelectedProduct',
 'public sealed class GingerCandidateSnapshot',
 'public sealed class GingerSearchContext',
 'public sealed class GingerFilterOnlyContext'
) | ForEach-Object { Definition $_ }
$methods=@(
 'public static void RequireSameSelectedProduct(',
 'private static bool SameBounds(',
 'public static void RequireSameCandidates(',
 'public static GingerCandidateSnapshot ReadMatchingCandidates(',
 'public static bool SearchWordsDescribeSource(',
 'private static GingerSearchContext ReadSearchContextOnce(',
 'public static void RequireSameSearchContext(',
 'public static GingerSearchContext ReadSearchContext(IntPtr root,uint process,int settleMilliseconds)',
 'private static GingerFilterOnlyContext ReadFilterOnlyContextOnce(',
 'private static void RequireSameFilterOnlyContext(',
 'public static GingerFilterOnlyContext ReadFilterOnlyContext('
) | ForEach-Object { Definition $_ }
# Exercise the actual product-versus-word limit branch independently of COM.
$reader=Definition 'private static GingerCandidateSnapshot ReadCandidatesOnce('
$limitStart=$reader.IndexOf('if(count>256)')
$limitEnd=$reader.IndexOf('IAccessible list=', $limitStart)
if($limitStart -lt 0 -or $limitEnd -le $limitStart){throw 'Candidate limit branch missing'}
$limit=$reader.Substring($limitStart,$limitEnd-$limitStart)
$code=@'
using System;
using System.Collections.Generic;
using System.Text;
public sealed class GingerCatalogueCandidate {
 public int ObservedPosition,StateImage;
 public string Title,DescriptionText,DefaultAction;
 public bool Selected,IdentityVerified;
 public bool? Checked;
 public int[] Bounds;
}
'@ + ($classes -join "`n") + @'

public static class GingerMenuAccess {
 public static string Mode="";
 public static int WordReads,ProductReads,CountReads,WindowChecks;
 public static long NativeProductCount=278,NativeWordCount=1;
 public static void Reset(string mode) {
  Mode=mode;WordReads=ProductReads=CountReads=WindowChecks=0;
  NativeProductCount=278;NativeWordCount=1;
 }
 private static GingerSelectedProduct Source(bool after) {
  var source=new GingerSelectedProduct {TableHandle=101,ObservedRowPosition=3,
   DocumentNumber="document",Supplier="supplier",Title="Alpha",PriceText="12",LinkedProductText="none"};
  if(Mode=="source_after" && after)source.Supplier="changed";
  if(WordReads>1) {
   if(Mode=="document_between")source.DocumentNumber="changed";
   if(Mode=="supplier_between")source.Supplier="changed";
   if(Mode=="row_between")source.ObservedRowPosition=4;
   if(Mode=="table_between")source.TableHandle=102;
   if(Mode=="price_between")source.PriceText="13";
   if(Mode=="linked_between")source.LinkedProductText="linked";
  }
  return source;
 }
 private static GingerCandidateSnapshot Words() {
  var row=new GingerCatalogueCandidate {ObservedPosition=1,Title="Alpha",DescriptionText="word",
   DefaultAction="toggle",StateImage=2,Checked=true,Bounds=new[]{0,0,100,20}};
  if(Mode=="stale_query")row.Title="Beta";
  if(WordReads>1) {
   if(Mode=="word_between")row.Title="Beta";
   if(Mode=="state_between"){row.Checked=false;row.StateImage=0;}
   if(Mode=="description_between")row.DescriptionText="changed";
   if(Mode=="bounds_between")row.Bounds[0]=5;
  }
  return new GingerCandidateSnapshot {Source=Source(false),PanelHandle=10,Candidates=new[]{row}};
 }
 private static GingerCandidateSnapshot ReadCandidatesOnce(IntPtr root,uint process,bool searchWords=false,GingerSelectedProduct adjacentSource=null) {
  if(searchWords){WordReads++;CheckReadLimit(NativeWordCount,true);return Words();}
  ProductReads++;CheckReadLimit(NativeProductCount,false);
  return new GingerCandidateSnapshot {Source=adjacentSource??Source(false),PanelHandle=20,Candidates=new GingerCatalogueCandidate[0]};
 }
 private static GingerSelectedProduct ReadSelectedProductOnce(IntPtr root,uint process) {return Source(true);}
 private static IEnumerable<IntPtr> Windows(IntPtr root,uint process) {
  if(process!=7)throw new InvalidOperationException("Wrong process");
  yield return new IntPtr(10);
  if(Mode=="missing")yield break;
  yield return new IntPtr(Mode=="same_panel"?10:Mode=="panel_between" && WordReads>1?21:20);
  if(Mode=="duplicate")yield return new IntPtr(30);
 }
 private static int GetClassNameW(IntPtr hwnd,StringBuilder name,int capacity) {name.Append("SysListView32");return name.Length;}
 private static bool IsWindowVisible(IntPtr hwnd) {return true;}
 private static IEnumerable<string[]> Headers(IntPtr hwnd,uint process) {
  if(hwnd.ToInt64()==10 && Mode!="same_panel")yield return new[]{"\u0441\u043b\u043e\u0432\u043e","\u043f\u043e\u0438\u0441\u043a"};
  else if(Mode=="dual_headers")yield return new[]{"\u0441\u043b\u043e\u0432\u043e","\u043f\u043e\u0438\u0441\u043a","\u0442\u043e\u0432\u0430\u0440","\u0446\u0435\u043d\u0430"};
  else yield return new[]{"\u0442\u043e\u0432\u0430\u0440","\u0446\u0435\u043d\u0430"};
 }
 private static int Column(string[] columns,string value) {return Array.IndexOf(columns,value);}
 private static long ListCount(IntPtr hwnd,uint process) {
  CountReads++;
  if(Mode=="owner_changed" && CountReads>1)throw new InvalidOperationException("Candidate count unavailable");
  if(Mode=="count_unavailable")throw new InvalidOperationException("Candidate count unavailable");
  if(Mode=="count_during" && CountReads==2)return 279;
  if(Mode=="count_between" && CountReads>=3)return 279;
  if(Mode=="bounded_between" && CountReads>=3)return 256;
  return NativeProductCount;
 }
 private static void RequireWindow(IntPtr root,uint process) {
  WindowChecks++;
  if(Mode=="window_changed")throw new InvalidOperationException("Window changed");
 }
 private static void CheckReadLimit(long count,bool searchWords) {
'@ + $limit + @'

 }
'@ + ($methods -join "`n") + @'

 private static void Check(bool value,string message) {if(!value)throw new Exception(message);}
 private static void Reject(string mode,string expected) {
  Reset(mode);
  try {ReadFilterOnlyContext(IntPtr.Zero,7,0);}
  catch(InvalidOperationException error) {
   Check(error.Message.Contains(expected),mode+": unexpected rejection "+error.Message);
   Check(ProductReads==0,mode+": product contents were enumerated");
   return;
  }
  throw new Exception(mode+": invalid context accepted");
 }
 private static GingerFilterOnlyContext Context() {
  return new GingerFilterOnlyContext {SearchWords=Words(),ProductPanelHandle=20,ProductRows=278,QueryWordsMatchSource=true};
 }
 public static void Run() {
  Reset("");
  var result=ReadFilterOnlyContext(IntPtr.Zero,7,0);
  Check(WordReads==2 && ProductReads==0 && CountReads==4 && WindowChecks==2,"Expected two full word reads and four scalar count checks only");
  Check(result.ProductRows==278 && result.ProductPanelHandle==20 && result.SearchWords.PanelHandle==10 && result.QueryWordsMatchSource,"Wrong filter-only metadata");
  Check(!result.CanMatch && !result.SearchCompletionVerified && !result.SearchWords.CanMatch && !result.SearchWords.IdentityVerified && !result.SearchWords.Source.CanMatch,"Unexpected authority claim");
  Check(typeof(GingerFilterOnlyContext).GetField("Products")==null && typeof(GingerFilterOnlyContext).GetField("Candidates")==null,"Filter-only context must not contain an empty/partial product list");
  foreach(long count in new long[]{257,278,1000000}) {
   Reset("");NativeProductCount=count;
   Check(ReadFilterOnlyContext(IntPtr.Zero,7,0).ProductRows==count && ProductReads==0,"Overflow count must not allocate/read product rows");
  }
  foreach(long count in new long[]{0,1,256}) {
   Reset("");NativeProductCount=count;
   bool rejected=false;
   try{ReadFilterOnlyContext(IntPtr.Zero,7,0);}catch(InvalidOperationException error){rejected=error.Message.Contains("complete observation required");}
   Check(rejected,"A bounded list must require the complete reader");
  }
  Reject("duplicate","Ambiguous candidate panels");
  Reject("missing","Candidate panel missing");
  Reject("same_panel","Ambiguous candidate panels");
  Reject("dual_headers","Ambiguous matching panel");
  Reject("count_during","count changed");
  Reject("count_between","metadata changed");
  Reject("bounded_between","complete observation required");
  Reject("panel_between","metadata changed");
  Reject("source_after","Selected product changed");
  foreach(string mode in new[]{"document_between","supplier_between","row_between","table_between","price_between","linked_between"})
   Reject(mode,"Selected product changed");
  Reject("word_between","do not describe");
  Reject("stale_query","do not describe");
  foreach(string mode in new[]{"state_between","description_between","bounds_between"})Reject(mode,"Candidate rows changed");
  Reject("owner_changed","count unavailable");
  Reject("count_unavailable","count unavailable");
  Reject("window_changed","Window changed");
  Reset("");
  foreach(var change in new Action<GingerFilterOnlyContext>[] {
   c=>c.CanMatch=true,c=>c.SearchCompletionVerified=true,c=>c.QueryWordsMatchSource=false,
   c=>c.ProductRows=256,c=>c.ProductPanelHandle=c.SearchWords.PanelHandle,
   c=>c.SearchWords.CanMatch=true,c=>c.SearchWords.IdentityVerified=true,
   c=>c.SearchWords.SearchCompletionVerified=true,c=>c.SearchWords.Source.CanMatch=true,
   c=>c.SearchWords.Source.IdentityVerified=true
  }) {
   var before=Context();var after=Context();change(after);
   bool rejected=false;
   try{RequireSameFilterOnlyContext(before,after);}catch(InvalidOperationException){rejected=true;}
   Check(rejected,"Invalid flag or partial-context claim accepted");
  }
  foreach(int settle in new[]{-1,1001}) {
   Reset("");bool rejected=false;
   try{ReadFilterOnlyContext(IntPtr.Zero,7,settle);}catch(ArgumentOutOfRangeException){rejected=true;}
   Check(rejected && WordReads==0,"Settling limit must reject before reads");
  }
  Reset("");bool wrongProcess=false;
  try{ReadFilterOnlyContext(IntPtr.Zero,8,0);}catch(InvalidOperationException error){wrongProcess=error.Message.Contains("Wrong process");}
  Check(wrongProcess,"Wrong process accepted");
  CheckReadLimit(256,false);CheckReadLimit(256,true);
  foreach(bool words in new[]{false,true}) {
   bool rejected=false;
   try{CheckReadLimit(257,words);}
   catch(GingerCandidateLimitException error){rejected=!words && error.ObservedCount==257;}
   catch(InvalidOperationException error){rejected=words && error.Message.Contains("Search word read limit");}
   Check(rejected,"Legacy limit256 or typed overflow distinction changed");
  }
  Reset("");NativeWordCount=257;bool wordLimit=false;
  try{ReadFilterOnlyContext(IntPtr.Zero,7,0);}
  catch(GingerCandidateLimitException){throw new Exception("Word overflow incorrectly enables filtering");}
  catch(InvalidOperationException error){wordLimit=error.Message.Contains("Search word read limit");}
  Check(wordLimit && CountReads==0,"Word overflow must remain a hard failure");
  foreach(bool context in new[]{false,true}) {
   Reset("");bool rejected=false;
   try{if(context)ReadSearchContext(IntPtr.Zero,7,0);else ReadMatchingCandidates(IntPtr.Zero,7);}
   catch(GingerCandidateLimitException error){rejected=error.ObservedCount==278;}
   Check(rejected && ProductReads==1 && CountReads==0,"Legacy full readers silently fell back or truncated overflow");
  }
 }
}
'@
if($code -match 'DllImport|SendInput|AccessibleObjectFromWindow|Process\.Start') {throw 'Synthetic harness unexpectedly contains a live capability'}
Add-Type -TypeDefinition $code
[GingerMenuAccess]::Run()
'PASS: extracted native filter-only contexts keep stable complete words, source/panel/count/flags guards; legacy limits stay strict; no product list or real UI'
