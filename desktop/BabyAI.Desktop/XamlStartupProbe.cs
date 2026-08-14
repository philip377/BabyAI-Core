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

        ("SECTION Orb", $"<Button {Xmlns} Width=\"112\" Height=\"112\" CornerRadius=\"56\" Padding=\"0\" Background=\"Transparent\" BorderBrush=\"#32FFFFFF\" BorderThickness=\"1\"><Grid><Ellipse Width=\"110\" Height=\"110\" Stroke=\"#20FFFFFF\" StrokeThickness=\"1\" /><Ellipse Width=\"104\" Height=\"104\" Opacity=\"0.28\" Fill=\"#7C8DFF\" /><Ellipse Width=\"88\" Height=\"88\" Opacity=\"0.96\" RenderTransformOrigin=\"0.5,0.5\"><Ellipse.RenderTransform><ScaleTransform ScaleX=\"1\" ScaleY=\"1\" /></Ellipse.RenderTransform><Ellipse.Fill><RadialGradientBrush Center=\"0.38,0.32\" GradientOrigin=\"0.38,0.32\" RadiusX=\"0.7\" RadiusY=\"0.7\"><GradientStop Color=\"#FFF7F9FF\" Offset=\"0\" /><GradientStop Color=\"#FFB8C8FF\" Offset=\"0.55\" /><GradientStop Color=\"#FF6D7CFF\" Offset=\"1\" /></RadialGradientBrush></Ellipse.Fill></Ellipse><Ellipse Width=\"66\" Height=\"66\" Margin=\"-12,-16,0,0\" Fill=\"#16FFFFFF\" /><TextBlock Text=\"•\" FontSize=\"32\" FontWeight=\"SemiBold\" Foreground=\"White\" /></Grid></Button>"),
        ("SECTION Panel shell", $"<Border {Xmlns} Width=\"360\" Height=\"420\" CornerRadius=\"26\" Background=\"#E0141720\" BorderBrush=\"#30FFFFFF\" BorderThickness=\"1\"><Border.Transitions><TransitionCollection><RepositionThemeTransition /></TransitionCollection></Border.Transitions><Grid Padding=\"16\" RowSpacing=\"10\" /></Border>"),
        ("SECTION Panel header", $"<Grid {Xmlns} ColumnSpacing=\"7\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions><StackPanel Grid.Column=\"0\" Spacing=\"2\"><TextBlock Text=\"BabyAI\" FontSize=\"21\" FontWeight=\"SemiBold\" Foreground=\"#FFF8FAFF\" /><TextBlock Text=\"Нет активной задачи\" FontSize=\"11\" Foreground=\"#9FFFFFFF\" TextWrapping=\"Wrap\" MaxLines=\"1\" /></StackPanel><Border Grid.Column=\"1\" CornerRadius=\"13\" Padding=\"9,5\" Background=\"#18FFFFFF\" BorderBrush=\"#22FFFFFF\" BorderThickness=\"1\"><TextBlock Text=\"Brain: checking…\" FontSize=\"10\" Foreground=\"#DFFFFFFF\" MaxWidth=\"128\" TextTrimming=\"CharacterEllipsis\" /></Border><Button Grid.Column=\"2\" Width=\"28\" Height=\"28\" Padding=\"0\" CornerRadius=\"14\" Content=\"⋯\" FontSize=\"17\" ToolTipService.ToolTip=\"Диагностика\" /></Grid>"),
        ("SECTION Reply", $"<Border {Xmlns} Background=\"#12FFFFFF\" BorderBrush=\"#18FFFFFF\" BorderThickness=\"1\" CornerRadius=\"12\" Padding=\"10,7\"><Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions><ProgressRing Grid.Column=\"0\" Width=\"14\" Height=\"14\" IsActive=\"False\" Visibility=\"Collapsed\" /><TextBlock Grid.Column=\"1\" Text=\"Готов к разговору.\" TextWrapping=\"Wrap\" FontSize=\"11\" Foreground=\"#AFFFFFFF\" /><Button Grid.Column=\"2\" Content=\"Повторить\" FontSize=\"11\" Padding=\"10,4\" CornerRadius=\"10\" Visibility=\"Collapsed\" /></Grid></Border>"),
        ("SECTION Conversation", $"<Border {Xmlns} Background=\"#0BFFFFFF\" BorderBrush=\"#12FFFFFF\" BorderThickness=\"1\" CornerRadius=\"18\" Padding=\"10,10,6,10\"><ScrollViewer VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\"><StackPanel Padding=\"1,2,5,2\" Spacing=\"2\" /></ScrollViewer></Border>"),
        ("SECTION Composer", $"<Border {Xmlns} Background=\"#16FFFFFF\" BorderBrush=\"#24FFFFFF\" BorderThickness=\"1\" CornerRadius=\"18\" Padding=\"10,7,7,7\"><Grid ColumnSpacing=\"7\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions><TextBox Grid.Column=\"0\" PlaceholderText=\"Напиши BabyAI...\" AcceptsReturn=\"True\" MinHeight=\"44\" MaxHeight=\"88\" Padding=\"2,7\" TextWrapping=\"Wrap\" Background=\"Transparent\" BorderThickness=\"0\" Foreground=\"#FFF8FAFF\" /><Button Grid.Column=\"1\" Width=\"42\" Height=\"42\" Padding=\"0\" CornerRadius=\"21\" Content=\"↑\" FontSize=\"20\" FontWeight=\"SemiBold\" ToolTipService.ToolTip=\"Отправить · Enter\" /><Button Grid.Column=\"2\" Width=\"42\" Height=\"42\" Padding=\"0\" CornerRadius=\"21\" Content=\"■\" FontSize=\"13\" ToolTipService.ToolTip=\"Остановить\" Visibility=\"Collapsed\" /></Grid></Border>"),
        ("SECTION Approval", $"<Grid {Xmlns} ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions><Button Grid.Column=\"0\" Content=\"Сохранить\" CornerRadius=\"12\" Visibility=\"Collapsed\" /><Button Grid.Column=\"1\" Content=\"Отклонить\" CornerRadius=\"12\" Visibility=\"Collapsed\" /></Grid>"),
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
