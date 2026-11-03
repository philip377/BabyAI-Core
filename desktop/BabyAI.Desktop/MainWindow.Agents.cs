using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _agentsDialogOpen;
    private readonly AgentProfileStore _agentStore = new();
    private readonly Dictionary<string, GingerCompanionWindow> _companions = new();
    private AgentProfile? _chatAgent;
    private GingerCompanionPhase _gingerPhase = GingerCompanionPhase.Idle;

    private void SetGingerPhase(GingerCompanionPhase phase)
    {
        _gingerPhase = phase;
        if (_companions.TryGetValue("ginger", out var pet)) pet.SetStatus(phase);
    }
    private void SetGingerCaptureMode(bool capturing)
    {
        foreach (var pet in _companions.Values) pet.SetCaptureMode(capturing);
    }
    private void CloseCompanions()
    {
        foreach (var pet in _companions.Values.ToArray()) pet.Close();
    }
    private void ShowCompanion(AgentProfile profile, PointInt32? position = null)
    {
        if (_companions.TryGetValue(profile.Id, out var existing))
        {
            if (position.HasValue) existing.AppWindow.Move(position.Value);
            existing.Activate();
            return;
        }
        var pet = new GingerCompanionWindow(profile, _agentStore.Load().Preferences);
        if (profile.Id == "ginger") pet.SetStatus(_gingerPhase);
        _companions.Add(profile.Id, pet);
        pet.PositionSaved += point =>
        {
            try
            {
                var catalog = _agentStore.Load();
                _agentStore.Save(catalog with { Agents = catalog.Agents.Select(a =>
                    a.Id == profile.Id ? a with { X = point.X, Y = point.Y } : a).ToList() });
            }
            catch (Exception ex) { StartupDiagnostics.Log("Pet position save failed: " + ex.Message); }
        };
        pet.Closed += (_, _) => _companions.Remove(profile.Id);
        pet.Activate();
        if (position.HasValue) pet.AppWindow.Move(position.Value);
    }
    private async void AgentsNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_agentsDialogOpen || _gingerDialogOpen) return;
        _agentsDialogOpen = true;
        var openGinger = false;
        try
        {
            var content = new StackPanel { Spacing = 12, MinWidth = 460 };
            var cards = new StackPanel { Spacing = 12 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var create = new Button { Content = "＋ Создать агента" };
            var editor = new StackPanel { Spacing = 9, Visibility = Visibility.Collapsed };
            var name = new TextBox { Header = "Имя", MaxLength = 60 };
            var task = new TextBox { Header = "Чем помогает", MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var appearance = new ComboBox { Header = "Внешний вид", ItemsSource = new[] { "Фиолетовый", "Мятный", "Янтарный" }, SelectedIndex = 0 };
            var save = new Button { Content = "Сохранить агента" };
            var cancel = new Button { Content = "Отмена" };
            string? editingId = null;
            editor.Children.Add(name); editor.Children.Add(task); editor.Children.Add(appearance);
            editor.Children.Add(save); editor.Children.Add(cancel);
            cancel.Click += (_, _) => editor.Visibility = Visibility.Collapsed;
            create.Click += (_, _) => { editingId = null; name.Text = ""; task.Text = ""; appearance.SelectedIndex = 0; editor.Visibility = Visibility.Visible; };
            ContentDialog? dialog = null;
            void Refresh()
            {
                cards.Children.Clear();
                foreach (var agent in _agentStore.Load().Agents)
                {
                    var card = new StackPanel { Spacing = 8 };
                    var title = new TextBlock { Text = "• ᴗ •   " + agent.Name, FontSize = 20 };
                    // Native pointer capture follows the real desktop cursor, including across monitors.
                    GingerCompanionWindow.AttachDesktopDrag(title, point => ShowCompanion(agent, point));
                    card.Children.Add(title);
                    card.Children.Add(new TextBlock { Text = agent.Task, TextWrapping = TextWrapping.Wrap });
                    card.Children.Add(new TextBlock { Text = agent.Id == "ginger" ? GingerCompanionWindow.DescribePhase(_gingerPhase) : "Задачи через общий чат UNIX · действующие разрешения", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var open = new Button { Content = agent.Id == "ginger" ? "Открыть" : "Дать задачу", IsEnabled = !_busy };
                    open.Click += (_, _) =>
                    {
                        openGinger = agent.Id == "ginger";
                        if (!openGinger) { _chatAgent = agent; MessageBox.PlaceholderText = "Задача для " + agent.Name; }
                        dialog!.Hide();
                    };
                    var pet = new Button { Content = _companions.ContainsKey(agent.Id) ? "Скрыть питомца" : "На рабочий стол" };
                    pet.Click += (_, _) =>
                    {
                        try { if (_companions.TryGetValue(agent.Id, out var window)) window.Close(); else ShowCompanion(agent); Refresh(); }
                        catch (Exception ex) { error.Text = ex.Message; }
                    };
                    var edit = new Button { Content = "Изменить" };
                    edit.Click += (_, _) => { editingId = agent.Id; name.Text = agent.Name; task.Text = agent.Task; appearance.SelectedIndex = Array.IndexOf(new[] { "violet", "mint", "amber" }, agent.Appearance); editor.Visibility = Visibility.Visible; };
                    actions.Children.Add(open); actions.Children.Add(pet); actions.Children.Add(edit);
                    if (agent.Id != "ginger")
                    {
                        var delete = new Button { Content = "Удалить" };
                        delete.Click += (_, _) =>
                        {
                            try
                            {
                                var catalog = _agentStore.Load();
                                _agentStore.Save(catalog with { Agents = catalog.Agents.Where(a => a.Id != agent.Id).ToList() });
                                if (_companions.TryGetValue(agent.Id, out var window)) window.Close();
                                if (_chatAgent?.Id == agent.Id) _chatAgent = null;
                                Refresh();
                            }
                            catch (Exception ex) { error.Text = ex.Message; }
                        };
                        actions.Children.Add(delete);
                    }
                    card.Children.Add(actions);
                    cards.Children.Add(new Border { Child = card, Padding = new Thickness(14), CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 127, 145, 232)) });
                }
            }
            save.Click += (_, _) =>
            {
                try
                {
                    var catalog = _agentStore.Load();
                    var previous = catalog.Agents.FirstOrDefault(a => a.Id == editingId);
                    var profile = new AgentProfile(editingId ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), task.Text.Trim(), new[] { "violet", "mint", "amber" }[appearance.SelectedIndex], previous?.X, previous?.Y);
                    var agents = catalog.Agents.Where(a => a.Id != profile.Id).ToList(); agents.Add(profile);
                    _agentStore.Save(catalog with { Agents = agents });
                    if (_companions.TryGetValue(profile.Id, out var window)) { window.Close(); ShowCompanion(profile); }
                    if (_chatAgent?.Id == profile.Id) _chatAgent = profile;
                    editor.Visibility = Visibility.Collapsed; error.Text = ""; Refresh();
                }
                catch (Exception ex) { error.Text = ex.Message; }
            };
            content.Children.Add(new TextBlock { Text = "Выбери агента или создай своего. Потяни его имя за пределы окна, чтобы разместить питомца на рабочем столе.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(create); content.Children.Add(editor); content.Children.Add(error); content.Children.Add(cards);
            Refresh();
            dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "UNIX · Агенты", Content = new ScrollViewer { Content = content, MaxHeight = 560 }, CloseButtonText = "Закрыть" };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { StartupDiagnostics.Log("Agents panel: " + ex.Message); ReplyText.Text = "Не удалось открыть агентов: " + ex.Message; }
        finally { _agentsDialogOpen = false; }
        if (openGinger) GingerNavigationButton_Click(sender, e);
    }
}
