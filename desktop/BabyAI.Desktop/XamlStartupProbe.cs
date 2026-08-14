using Microsoft.UI.Xaml.Markup;

namespace BabyAI.Desktop;

internal static class XamlStartupProbe
{
    private const string Xmlns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";

    private static readonly (string Name, string Markup)[] Cases =
    [
        ("ProgressRing bare", $"<ProgressRing {Xmlns} />"),
        ("ProgressRing with size", $"<ProgressRing {Xmlns} Width=\"14\" Height=\"14\" />"),
        ("ProgressRing IsActive=False", $"<ProgressRing {Xmlns} IsActive=\"False\" />"),
        ("ProgressRing Visibility=Collapsed", $"<ProgressRing {Xmlns} Visibility=\"Collapsed\" />"),
        ("ProgressRing exact MainWindow", $"<ProgressRing {Xmlns} Width=\"14\" Height=\"14\" IsActive=\"False\" Visibility=\"Collapsed\" VerticalAlignment=\"Center\" />"),
    ];

    internal static void Run()
    {
        StartupDiagnostics.Log($"XAML ProgressRing probe begin ({Cases.Length} cases)");
        foreach (var (name, markup) in Cases)
        {
            StartupDiagnostics.Log($"XAML-PROBE START: {name}");
            try
            {
                _ = XamlReader.Load(markup);
                StartupDiagnostics.Log($"XAML-PROBE PASS: {name}");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"XAML-PROBE FAIL: {name}; hresult=0x{ex.HResult:X8}; message={ex.Message}", ex);
            }
        }
        StartupDiagnostics.Log("XAML ProgressRing probe end");
    }
}
