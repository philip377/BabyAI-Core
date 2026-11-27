using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private GingerProgressBinding? _gingerProgressBinding;

    private GingerProgressTurn CreateGingerProgressTurn()
    {
        _gingerProgressBinding?.View.Detach();
        _gingerProgressBinding = null;
        var view = new GingerProgressTurn(UpdateGingerProgressTurn);
        var index = CreateConversationTurn("UNIX", view.Transcript);
        _gingerProgressBinding = new(view, _selectedChatId, index, _conversation[index]);
        RestoreGingerProgressTurn();
        return view;
    }

    private void UpdateGingerProgressTurn(GingerProgressTurn view, bool finished)
    {
        if (!FindGingerProgressTurn() || _gingerProgressBinding is not { } binding || binding.View != view) return;
        binding.Entry = "UNIX: " + view.Transcript;
        _conversation[binding.Index] = binding.Entry;
        if (finished) RenderConversation();
        else if (ConversationScroller.ScrollableHeight - ConversationScroller.VerticalOffset < 80)
        {
            ConversationScroller.UpdateLayout();
            ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null);
        }
    }

    // Called explicitly after the ordinary transcript renderer. A view is bound
    // to its original string instance as well as the chat; reloaded history,
    // identical text in another chat, and a trimmed turn cannot adopt it.
    private void RestoreGingerProgressTurn()
    {
        if (!FindGingerProgressTurn() || _gingerProgressBinding is not { } binding) return;
        if (ConversationStack.Children.Count != _conversation.Count) return;
        if (ReferenceEquals(ConversationStack.Children[binding.Index], binding.View.Card)) return;
        ConversationStack.Children.RemoveAt(binding.Index);
        ConversationStack.Children.Insert(binding.Index, binding.View.Card);
    }

    private bool FindGingerProgressTurn()
    {
        if (_gingerProgressBinding is not { } binding) return false;
        if (binding.ChatId == _selectedChatId)
        {
            var index = _conversation.FindIndex(entry => ReferenceEquals(entry, binding.Entry));
            if (index >= 0)
            {
                binding.Index = index;
                return true;
            }
        }
        binding.View.Detach();
        _gingerProgressBinding = null;
        return false;
    }

    private sealed class GingerProgressBinding(GingerProgressTurn view, string? chatId, int index, string entry)
    {
        public GingerProgressTurn View { get; } = view;
        public string? ChatId { get; } = chatId;
        public int Index { get; set; } = index;
        public string Entry { get; set; } = entry;
    }
}

// Owns only presentation. It never controls Ginger, a cancellation token or a
// confirmation, and history cards never start an animation.
internal sealed class GingerProgressTurn
{
    private readonly StackPanel _steps = new() { Spacing = 9 };
    private readonly TextBlock _summary = Text("Готовлю задачу Ginger…", 13);
    private readonly TextBlock _details = Text("", 12);
    private readonly List<StepRow> _rows = [];
    private string _journal = "";
    private readonly Expander _expander;
    private readonly Button _copy;
    private Action<GingerProgressTurn, bool>? _changed;
    private Storyboard? _animation;
    private bool _finished;
    private readonly bool _history;
    private StepRow? _current;

    internal StackPanel Card { get; }
    internal string Transcript => GingerProgressTranscript.Format(new(
        _summary.Text, _details.Text, _rows.Select(row => new GingerProgressStep(row.Label.Text, row.State)).ToArray()));

    internal GingerProgressTurn(Action<GingerProgressTurn, bool>? changed = null, GingerProgressSnapshot? history = null)
    {
        _changed = changed;
        _history = history is not null;
        _finished = _history;
        _summary.MaxLines = 4;
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        _expander = new Expander
        {
            Header = "Подробности и сравнение",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            FontSize = 11,
            Visibility = Visibility.Collapsed,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _details
            }
        };
        AutomationProperties.SetName(_expander, "Подробности работы Ginger и сравнение кандидатов");
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_steps);
        body.Children.Add(_summary);
        body.Children.Add(_expander);
        Card = new StackPanel
        {
            MaxWidth = 336, HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 4, Margin = new Thickness(0, 0, 0, 10)
        };
        Card.Children.Add(new TextBlock
        {
            Text = "UNIX · GINGER", FontSize = 9, CharacterSpacing = 105,
            FontWeight = FontWeights.SemiBold, Foreground = Brush(190, 202, 255, 135),
            Margin = new Thickness(8, 0, 8, 0)
        });
        Card.Children.Add(new Border
        {
            MaxWidth = 326, Padding = new Thickness(13, 11, 13, 11),
            CornerRadius = new CornerRadius(18, 18, 18, 7),
            Background = Brush(255, 255, 255, 19), BorderBrush = Brush(171, 187, 255, 22),
            BorderThickness = new Thickness(1), Child = body
        });
        _copy = new Button
        {
            Content = "Копировать", FontSize = 9, Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(5, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(255, 255, 255, 0), BorderThickness = new Thickness(0),
            Foreground = Brush(255, 255, 255, 92)
        };
        ToolTipService.SetToolTip(_copy, "Копировать итог и полные подробности");
        _copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(_summary.Text + (string.IsNullOrWhiteSpace(_details.Text) ? "" : "\n\n" + _details.Text));
            Clipboard.SetContent(package);
            _copy.Content = "Скопировано";
        };
        Card.Children.Add(_copy);
        Card.Loaded += (_, _) => AnimateCurrent();
        Card.Unloaded += (_, _) => StopAnimation();
        if (history is not null)
        {
            _summary.Text = history.Summary;
            _details.Text = GingerProgressTranscript.LimitDetails(history.Details);
            foreach (var step in history.Steps) AddRow("history", step.Label, step.State);
            _expander.Visibility = history.Details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    internal static bool TryCreateHistoryCard(string text, out UIElement? card)
    {
        card = null;
        if (!GingerProgressTranscript.TryParse(text, out var snapshot)) return false;
        card = new GingerProgressTurn(history: snapshot).Card;
        return true;
    }

    internal void Report(GingerChatStage stage, string text)
    {
        var label = stage switch
        {
            GingerChatStage.Started => "Задача принята",
            GingerChatStage.Reading => "Читаю строку Ginger",
            GingerChatStage.Filtering => "Подготавливаю поиск",
            GingerChatStage.Comparing => "Сравниваю кандидатов",
            GingerChatStage.Review => "Ответ готов к проверке",
            GingerChatStage.Empty => "Кандидаты не найдены",
            GingerChatStage.Skipping => "Проверка строки завершена",
            GingerChatStage.Completed => "Проход завершён",
            GingerChatStage.Cancelled => "Остановлено",
            _ => "Не удалось завершить"
        };
        Advance(stage.ToString(), label, text,
            stage is GingerChatStage.Review or GingerChatStage.Empty or GingerChatStage.Skipping or GingerChatStage.Completed,
            stage is GingerChatStage.Cancelled or GingerChatStage.Failed,
            stage == GingerChatStage.Cancelled);
    }

    internal void Report(GingerChatMatchStage stage, string text) => Advance("match:" + stage, stage switch
    {
        GingerChatMatchStage.Checking => "Проверяю выбранную пару",
        GingerChatMatchStage.Matching => "Сопоставляю подтверждённую пару",
        _ => "Сопоставление проверено"
    }, text, stage == GingerChatMatchStage.Verified, false, false);

    private void Advance(string key, string label, string text, bool completed, bool failed, bool cancelled)
    {
        if (_finished) return;
        StopAnimation();
        if (!string.IsNullOrWhiteSpace(text))
            _journal = GingerProgressTranscript.LimitDetails(_journal +
                (_journal.Length == 0 ? "" : Environment.NewLine) + text.Trim());
        _details.Text = _journal;
        _expander.Visibility = _details.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _copy.Content = "Копировать";
        var failedCurrent = false;
        if (_current is { State: GingerProgressState.Running } previous)
        {
            if (failed)
            {
                SetState(previous, GingerProgressState.Failed, cancelled ? "остановлено" : "ошибка");
                failedCurrent = true;
            }
            else if (previous.Key != key || completed) SetState(previous, GingerProgressState.Completed);
        }
        if (!failed || !failedCurrent)
        {
            if (_current is null || _current.Key != key || _current.State != GingerProgressState.Running)
                AddRow(key, label, failed ? GingerProgressState.Failed : completed ? GingerProgressState.Completed : GingerProgressState.Running);
        }
        if (_current is not null) ToolTipService.SetToolTip(_current.Panel, text);
        _summary.Visibility = failed || completed ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = label;
        if (failed) _finished = true;
        AnimateCurrent();
        _changed?.Invoke(this, false);
    }

    internal void Finish(string summary, string details, string? failureStatus = null)
    {
        StopAnimation();
        _finished = true;
        // If no terminal event arrived, stop the indicator without claiming the
        // unfinished step succeeded. Normal success reports Review/Completed.
        if (_current is { State: GingerProgressState.Running } current)
            SetState(current, GingerProgressState.Failed, failureStatus ?? "не завершено");
        else if (_current is { State: GingerProgressState.Failed } failed && !string.IsNullOrWhiteSpace(failureStatus))
            SetState(failed, GingerProgressState.Failed, failureStatus);
        _summary.Text = string.IsNullOrWhiteSpace(summary) ? "Работа Ginger завершена." : summary.Trim();
        _summary.Visibility = Visibility.Visible;
        _details.Text = GingerProgressTranscript.LimitDetails(string.IsNullOrWhiteSpace(details) ? _journal : details.Trim());
        _expander.Visibility = _details.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _copy.Content = "Копировать";
        _changed?.Invoke(this, true);
    }

    internal void Detach()
    {
        StopAnimation();
        _changed = null;
        _finished = true;
    }

    private void AddRow(string key, string label, GingerProgressState state)
    {
        if (_rows.Count == GingerProgressTranscript.MaxSteps)
        {
            _rows.RemoveAt(0);
            _steps.Children.RemoveAt(0);
        }
        var indicator = Text("", 12);
        indicator.Width = 16;
        indicator.IsTextSelectionEnabled = false;
        var title = Text(label, 12);
        var status = Text("", 10);
        status.Foreground = Brush(211, 218, 239, 170);
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(title);
        text.Children.Add(status);
        var panel = new Grid { ColumnSpacing = 7 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(indicator);
        Grid.SetColumn(text, 1);
        panel.Children.Add(text);
        var row = new StepRow(key, panel, indicator, title, status);
        _rows.Add(row);
        _steps.Children.Add(panel);
        _current = row;
        SetState(row, state);
    }

    private void SetState(StepRow row, GingerProgressState state, string? status = null)
    {
        row.State = state;
        row.Indicator.Text = state switch { GingerProgressState.Completed => "✓", GingerProgressState.Failed => "!", _ => "•" };
        row.Indicator.Foreground = state switch
        {
            GingerProgressState.Completed => Brush(149, 203, 178, 235),
            GingerProgressState.Failed => Brush(242, 179, 148, 235),
            _ => Brush(160, 174, 255, 255)
        };
        row.Status.Text = status ?? (state switch
        {
            GingerProgressState.Completed => "готово",
            GingerProgressState.Failed => "не завершено",
            _ => _history ? "не завершено" : "выполняется"
        });
        AutomationProperties.SetName(row.Panel, row.Label.Text + ", " + row.Status.Text);
        AutomationProperties.SetLiveSetting(row.Status, AutomationLiveSetting.Polite);
    }

    private void AnimateCurrent()
    {
        StopAnimation();
        if (_finished || _history || !Card.IsLoaded || _current is not { State: GingerProgressState.Running } current ||
            !new UISettings().AnimationsEnabled) return;
        var pulse = new DoubleAnimation
        {
            From = 0.35, To = 1, AutoReverse = true,
            Duration = new Duration(TimeSpan.FromMilliseconds(650)), RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(pulse, current.Indicator);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        _animation = new Storyboard();
        _animation.Children.Add(pulse);
        _animation.Begin();
    }

    private void StopAnimation()
    {
        _animation?.Stop();
        _animation = null;
        if (_current is not null) _current.Indicator.Opacity = 1;
    }

    private static TextBlock Text(string text, double size) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        Foreground = Brush(248, 250, 255, 238), LineHeight = size + 6
    };

    private static SolidColorBrush Brush(byte red, byte green, byte blue, byte alpha) => new(Color.FromArgb(alpha, red, green, blue));

    private sealed class StepRow(string key, Grid panel, TextBlock indicator, TextBlock label, TextBlock status)
    {
        internal string Key { get; } = key;
        internal Grid Panel { get; } = panel;
        internal TextBlock Indicator { get; } = indicator;
        internal TextBlock Label { get; } = label;
        internal TextBlock Status { get; } = status;
        internal GingerProgressState State { get; set; }
    }
}
