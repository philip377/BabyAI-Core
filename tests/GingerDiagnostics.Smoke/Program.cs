using System.Diagnostics;
using System.Text;
using BabyAI.Desktop;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 2 && args[0] == "--reports")
{
    var reports = new List<GingerProbeResult>();
    foreach (var probe in GingerDiagnostics.Probes)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], Path.GetFileNameWithoutExtension(probe) + ".json")));
        reports.Add(GingerReadiness.Parse(probe, doc.RootElement));
    }
    Console.WriteLine(JsonSerializer.Serialize(GingerReadiness.Assess(reports)));
    return;
}

// Read-only row review finalization with a bounded hidden subprocess.
if(args.Length >= 1 && args[0] == "--finish-row-review")
{
    if(args.Length != 3)throw new InvalidOperationException("Row journal and preparation directory required");
    var script=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../scripts/windows/ginger-row-step.ps1"));
    var start=new ProcessStartInfo {
        FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"),
        UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
        StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
    };
    foreach(var argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Operation","FinishReview","-QueueDirectory",Path.GetFullPath(args[1]),"-PreparationDirectory",Path.GetFullPath(args[2])})
        start.ArgumentList.Add(argument);
    Console.WriteLine(await GingerDiagnostics.RunAsync(start,TimeSpan.FromSeconds(120),CancellationToken.None));
    return;
}

// Bounded persistent filter controller; no default CI input.
if(args.Length >= 1 && args[0] == "--filter-controller")
{
    if(args.Length is < 3 or > 4 || args[1] is not ("Step" or "Observe" or "Status") ||
       (args[1] == "Step" && (args.Length != 4 || args[3] != "--confirm-navigation")) ||
       (args[1] != "Step" && args.Length != 3))
        throw new InvalidOperationException("Explicit filter controller arguments required");
    var script=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../scripts/windows/ginger-filter-step.ps1"));
    var start=new ProcessStartInfo {
        FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"),
        UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
        StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
    };
    foreach(var argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Operation",args[1],"-PlanDirectory",Path.GetFullPath(args[2])})
        start.ArgumentList.Add(argument);
    if(args[1] == "Step")start.ArgumentList.Add("-ConfirmNavigation");
    Console.WriteLine(await GingerDiagnostics.RunAsync(start,TimeSpan.FromSeconds(120),CancellationToken.None));
    return;
}

// Single, explicitly confirmed search filter trial. No default CI input.
if (args.Length >= 1 && (args[0] == "--enable-search-word" || args[0] == "--disable-search-word"))
{
    if(args.Length != 4 || args[3] != "--confirm-navigation")
        throw new InvalidOperationException("Explicit filter confirmation required");
    var script=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../scripts/windows/ginger-search-star.ps1"));
    var start=new ProcessStartInfo {
        FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"),
        UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
        StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
    };
    foreach(var argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Word",args[1],"-ReceiptDirectory",Path.GetFullPath(args[2]),"-ConfirmNavigation"})
        start.ArgumentList.Add(argument);
    if(args[0] == "--disable-search-word") start.ArgumentList.Add("-Disable");
    Console.WriteLine(await GingerDiagnostics.RunAsync(start,TimeSpan.FromSeconds(120),CancellationToken.None));
    return;
}

// Explicit row receipt trial. Default CI never invokes this branch.
if (args.Length >= 1 && args[0] == "--row-controller")
{
    if (args.Length is < 3 or > 4 || args[1] is not ("Initialize" or "Step" or "Recover" or "Observe" or "Read" or "InspectSearch" or "InspectQuery" or "InspectSelection" or "PrepareFilters" or "Status") ||
        (args[1] is "Step" or "Recover" && (args.Length != 4 || args[3] != "--confirm-navigation")) ||
        (args[1] is not ("Step" or "Recover") && args.Length != 3))
        throw new InvalidOperationException("Explicit row operation and navigation confirmation required");
    var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../scripts/windows/ginger-row-step.ps1"));
    var start = new ProcessStartInfo {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", script, "-Operation", args[1], "-QueueDirectory", Path.GetFullPath(args[2]) })
        start.ArgumentList.Add(argument);
    if (args[1] is "Step" or "Recover") start.ArgumentList.Add("-ConfirmNavigation");
    Console.WriteLine(await GingerDiagnostics.RunAsync(start, TimeSpan.FromSeconds(60), CancellationToken.None));
    return;
}

// Explicit manual integration trial only. Default CI never drives a live application.
if (args.Length >= 1 && args[0] == "--queue-controller")
{
    if (args.Length is < 3 or > 4 || args[1] is not ("Initialize" or "Step" or "ReadCurrent" or "Status") ||
        (args[1] is "Step" or "ReadCurrent" && (args.Length != 4 || args[3] != "--confirm-navigation")) ||
        (args[1] is not ("Step" or "ReadCurrent") && args.Length != 3))
        throw new InvalidOperationException("Explicit queue operation and navigation confirmation required");
    var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../scripts/windows/ginger-queue-step.ps1"));
    if (!File.Exists(script)) throw new FileNotFoundException("Queue controller checkout missing");
    var start = new ProcessStartInfo {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", script, "-Operation", args[1], "-QueueDirectory", Path.GetFullPath(args[2]) })
        start.ArgumentList.Add(argument);
    if (args[1] is "Step" or "ReadCurrent") start.ArgumentList.Add("-ConfirmNavigation");
    Console.WriteLine(await GingerDiagnostics.RunAsync(start, TimeSpan.FromSeconds(60), CancellationToken.None));
    return;
}

if (args.Length == 1 && args[0] == "--live-candidates")
{
    if (!await GingerCandidateReader.TryReadAsync(Console.WriteLine, CancellationToken.None))
        throw new InvalidOperationException("Live candidate inspection unavailable");
    return;
}

CandidateReaderTests.Run();
ReadinessTests.Run();
NativeSelectionTests.Run();

static ProcessStartInfo Command(string command)
{
    var info = new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        info.ArgumentList.Add(arg);
    return info;
}

var output = await GingerDiagnostics.RunAsync(Command("[Console]::Out.Write('ok')"),
    TimeSpan.FromSeconds(10), CancellationToken.None);
if (output != "ok") throw new Exception("Output was not captured");

async Task MustFail(string command, TimeSpan timeout, CancellationToken token)
{
    var failed = false;
    var watch = Stopwatch.StartNew();
    try { await GingerDiagnostics.RunAsync(Command(command), timeout, token); }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or OperationCanceledException)
    { failed = true; }
    if (!failed) throw new Exception("Expected failure");
    if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Process cleanup hung");
}

await MustFail("exit 7", TimeSpan.FromSeconds(10), CancellationToken.None);
await MustFail("Start-Sleep -Seconds 60", TimeSpan.FromMilliseconds(500), CancellationToken.None);
using var cancel = new CancellationTokenSource(500);
await MustFail("Start-Sleep -Seconds 60", TimeSpan.FromSeconds(30), cancel.Token);
await MustFail("[Console]::Out.Write(('x' * 4100000)); Start-Sleep -Seconds 60",
    TimeSpan.FromSeconds(10), CancellationToken.None);
await MustFail("[Console]::Error.Write(('x' * 4100000)); Start-Sleep -Seconds 60",
    TimeSpan.FromSeconds(10), CancellationToken.None);
Console.WriteLine("PASS: output, exit failure, timeout, cancellation, stdout/stderr limits");
