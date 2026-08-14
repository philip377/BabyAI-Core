using Microsoft.UI.Xaml.Markup;

namespace BabyAI.Desktop;

internal static class XamlStartupProbe
{
    private const string Xmlns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";

    private static readonly (string Name, string Markup)[] Cases =
    [
        ("Grid.RowSpacing", $"<Grid {Xmlns} RowSpacing=\"10\" />"),
        ("Grid.ColumnSpacing", $"<Grid {Xmlns} ColumnSpacing=\"7\" />"),
        ("StackPanel.Spacing", $"<StackPanel {Xmlns} Spacing=\"2\" />"),
        ("StackPanel.Padding", $"<StackPanel {Xmlns} Padding=\"1,2,5,2\" />"),
        ("Button.CornerRadius", $"<Button {Xmlns} CornerRadius=\"14\" Content=\"x\" />"),
        ("Border.CornerRadius", $"<Border {Xmlns} CornerRadius=\"18\" />"),
        ("ToolTipService.ToolTip", $"<Button {Xmlns} ToolTipService.ToolTip=\"Диагностика\" Content=\"x\" />"),
        ("TextBlock.MaxLines", $"<TextBlock {Xmlns} Text=\"x\" TextWrapping=\"Wrap\" MaxLines=\"1\" />"),
        ("TextBlock.TextTrimming", $"<TextBlock {Xmlns} Text=\"x\" TextTrimming=\"CharacterEllipsis\" />"),
        ("ProgressRing", $"<ProgressRing {Xmlns} Width=\"14\" Height=\"14\" IsActive=\"False\" Visibility=\"Collapsed\" />"),
        ("TextBox.PlaceholderText", $"<TextBox {Xmlns} PlaceholderText=\"Напиши BabyAI...\" AcceptsReturn=\"True\" />"),
        ("ARGB brushes", $"<Border {Xmlns} Background=\"#E0141720\" BorderBrush=\"#30FFFFFF\" />"),
        ("RadialGradientBrush", $"<Ellipse {Xmlns} Width=\"88\" Height=\"88\"><Ellipse.Fill><RadialGradientBrush Center=\"0.38,0.32\" GradientOrigin=\"0.38,0.32\" RadiusX=\"0.7\" RadiusY=\"0.7\"><GradientStop Color=\"#FFF7F9FF\" Offset=\"0\" /><GradientStop Color=\"#FFB8C8FF\" Offset=\"0.55\" /><GradientStop Color=\"#FF6D7CFF\" Offset=\"1\" /></RadialGradientBrush></Ellipse.Fill></Ellipse>"),
        ("ScaleTransform", $"<Ellipse {Xmlns} Width=\"88\" Height=\"88\" RenderTransformOrigin=\"0.5,0.5\"><Ellipse.RenderTransform><ScaleTransform ScaleX=\"1\" ScaleY=\"1\" /></Ellipse.RenderTransform></Ellipse>"),
        ("Border.Transitions.RepositionThemeTransition", $"<Border {Xmlns}><Border.Transitions><TransitionCollection><RepositionThemeTransition /></TransitionCollection></Border.Transitions></Border>"),
        ("ScrollViewer bars", $"<ScrollViewer {Xmlns} VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\"><StackPanel /></ScrollViewer>"),
        ("FontWeight.SemiBold", $"<TextBlock {Xmlns} Text=\"x\" FontWeight=\"SemiBold\" />"),
        ("Panel header composite", $"<Grid {Xmlns} ColumnSpacing=\"7\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions><StackPanel Grid.Column=\"0\" Spacing=\"2\"><TextBlock Text=\"BabyAI\" FontWeight=\"SemiBold\" /><TextBlock Text=\"Нет активной задачи\" TextWrapping=\"Wrap\" MaxLines=\"1\" /></StackPanel><Border Grid.Column=\"1\" CornerRadius=\"13\" Padding=\"9,5\"><TextBlock Text=\"Brain: checking…\" TextTrimming=\"CharacterEllipsis\" /></Border></Grid>"),
        ("Reply composite", $"<Border {Xmlns} CornerRadius=\"12\" Padding=\"10,7\"><Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions><ProgressRing Grid.Column=\"0\" Width=\"14\" Height=\"14\" IsActive=\"False\" Visibility=\"Collapsed\" /><TextBlock Grid.Column=\"1\" Text=\"Готов к разговору.\" TextWrapping=\"Wrap\" /></Grid></Border>"),
        ("Composer composite", $"<Border {Xmlns} CornerRadius=\"18\" Padding=\"10,7,7,7\"><Grid ColumnSpacing=\"7\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions><TextBox Grid.Column=\"0\" PlaceholderText=\"Напиши BabyAI...\" AcceptsReturn=\"True\" MinHeight=\"44\" MaxHeight=\"88\" TextWrapping=\"Wrap\" /><Button Grid.Column=\"1\" Width=\"42\" Height=\"42\" CornerRadius=\"21\" Content=\"↑\" ToolTipService.ToolTip=\"Отправить · Enter\" /></Grid></Border>"),
    ];

    internal static void Run()
    {
        StartupDiagnostics.Log($"XAML probe begin ({Cases.Length} cases)");
        foreach (var (name, markup) in Cases)
        {
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
        StartupDiagnostics.Log("XAML probe end");
    }
}
