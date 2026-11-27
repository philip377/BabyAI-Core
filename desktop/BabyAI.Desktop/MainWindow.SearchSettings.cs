using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private UIElement BuildSearchSettings()
    {
        var panel = CreateSettingsPage("Интернет-поиск", "Фотографии флаконов и сведения об упаковке из публичных источников.");
        (string Mode, string Label)[] modes = [
            ("duckduckgo", "DuckDuckGo · без ключа, возможна CAPTCHA"),
            ("brave", "Brave · нужен ключ доступа"),
            ("links_only", "Только по готовой ссылке")
        ];
        var selector = new ComboBox { Header = "Как искать", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var mode in modes) selector.Items.Add(new ComboBoxItem { Content = mode.Label, Tag = mode.Mode });
        var key = new PasswordBox { Header = "Новый ключ Brave", PlaceholderText = "Оставь пустым, чтобы сохранить прежний ключ",
            MaxLength = 512, PasswordRevealMode = PasswordRevealMode.Hidden, Visibility = Visibility.Collapsed };
        var keyState = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var status = new TextBlock { Text = "Читаю настройки…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var save = new Button { Content = "Сохранить", IsEnabled = false };
        var check = new Button { Content = "Проверить подключение", IsEnabled = false };
        var clear = new Button { Content = "Удалить сохранённый ключ", IsEnabled = false };
        var stop = new Button { Content = "Остановить проверку", Visibility = Visibility.Collapsed };
        var controls = new StackPanel { Spacing = 8 };
        controls.Children.Add(selector);
        controls.Children.Add(key);
        controls.Children.Add(keyState);
        var buttons = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
        buttons.Children.Add(save);
        buttons.Children.Add(check);
        controls.Children.Add(buttons);
        controls.Children.Add(stop);
        controls.Children.Add(clear);
        controls.Children.Add(status);
        panel.Children.Add(CreateSettingsCard(controls));
        panel.Children.Add(new HyperlinkButton { Content = "Получить ключ на сайте Brave",
            NavigateUri = new Uri("https://api-dashboard.search.brave.com/") });
        panel.Children.Add(CreateSettingsNote("Ключ хранится под защитой твоей учётной записи Windows. В чат и модель он не передаётся. "
            + "Доступ и лимиты зависят от тарифа Brave; UNIX ничего не оформляет автоматически."));
        panel.Children.Add(CreateSettingsNote("Проверка отправит один запрос «EX NIHILO Blue Talisman perfume bottle». "
            + "Сохранение настроек само по себе не обращается в интернет. Готовую ссылку можно использовать при любом режиме."));

        var lifetime = new CancellationTokenSource();
        CancellationTokenSource? checking = null;
        bool active = true, busy = true, loading = true, dirty = false, savedKey = false, configured = false;
        string Mode() => (selector.SelectedItem as ComboBoxItem)?.Tag as string ?? "links_only";
        void UpdateControls()
        {
            selector.IsEnabled = !busy;
            key.IsEnabled = !busy;
            key.Visibility = Mode() == "brave" ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = !busy && dirty;
            check.IsEnabled = !busy && !dirty && configured && Mode() != "links_only";
            clear.IsEnabled = !busy && (savedKey || !configured);
            stop.Visibility = checking is null ? Visibility.Collapsed : Visibility.Visible;
        }
        void Apply(JsonElement result)
        {
            loading = true;
            var mode = result.GetProperty("mode").GetString();
            selector.SelectedIndex = Array.FindIndex(modes, m => m.Mode == mode);
            if (selector.SelectedIndex < 0) throw new InvalidDataException("Неизвестный режим поиска.");
            key.Password = "";
            savedKey = result.GetProperty("saved_key").GetBoolean();
            configured = result.GetProperty("configured").GetBoolean();
            clear.Content = savedKey ? "Удалить сохранённый ключ" : "Сбросить настройки поиска";
            var origin = result.GetProperty("key_source").GetString();
            keyState.Text = origin == "environment" ? "Используется ключ из параметров запуска. Он не сохранён в этом окне."
                : savedKey ? "Есть сохранённый защищённый ключ." : "Сохранённого ключа нет.";
            var problem = result.GetProperty("problem").GetString();
            status.Text = string.IsNullOrEmpty(problem) ? "Настройки прочитаны. Подключение ещё не проверено." : problem;
            dirty = false;
            loading = false;
        }
        void Changed()
        {
            if (!loading && !busy)
            {
                dirty = true;
                status.Text = "Есть изменения. Сначала сохрани настройки, затем проверь подключение.";
            }
            UpdateControls();
        }
        selector.SelectionChanged += (_, _) => Changed();
        key.PasswordChanged += (_, _) => Changed();
        panel.Unloaded += (_, _) => { active = false; lifetime.Cancel(); key.Password = ""; };
        panel.Loaded += async (_, _) =>
        {
            try
            {
                var result = await _bridge.SearchSettingsAsync("web.search.status", cancellationToken: lifetime.Token);
                if (active) Apply(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (active) status.Text = "Не удалось прочитать настройки. Закрой и снова открой этот раздел."; }
            finally { loading = false; busy = false; if (active) UpdateControls(); }
        };
        async Task SaveAsync(bool remove)
        {
            if (busy || !active) return;
            busy = true;
            UpdateControls();
            try
            {
                // Removing the saved key also disables web search; an environment
                // key cannot silently reactivate it. Existing links keep working.
                var mode = remove ? "links_only" : Mode();
                var value = remove || mode != "brave" || key.Password.Length == 0 ? null : key.Password;
                var request = _bridge.SearchSettingsAsync("web.search.configure", mode, value, remove);
                key.Password = "";
                var result = await request;
                if (active)
                {
                    Apply(result);
                    if (configured) status.Text = remove ? "Сохранённый ключ удалён. Включён режим по ссылке."
                        : "Сохранено. Новые запросы используют эти настройки без перезапуска. Подключение ещё не проверено.";
                }
            }
            catch (Exception) { if (active) status.Text = "Не удалось сохранить настройки. Проверь ключ и повторно открой раздел для проверки состояния."; }
            finally { busy = false; if (active) UpdateControls(); }
        }
        save.Click += async (_, _) => await SaveAsync(false);
        clear.Click += async (_, _) => await SaveAsync(true);
        stop.Click += (_, _) => checking?.Cancel();
        check.Click += async (_, _) =>
        {
            if (busy || dirty || !active) return;
            busy = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            checking = cancellation;
            status.Text = "Проверяю одним запросом…";
            UpdateControls();
            try
            {
                var result = await _bridge.SearchSettingsAsync("web.search.check", cancellationToken: cancellation.Token);
                if (active && !cancellation.IsCancellationRequested)
                    status.Text = result.GetProperty("message").GetString() + " Время: "
                        + DateTimeOffset.Parse(result.GetProperty("checked_at").GetString()!).ToLocalTime().ToString("t");
            }
            catch (OperationCanceledException) { if (active) status.Text = "Проверка остановлена. Повторного запроса нет."; }
            catch (Exception) { if (active) status.Text = "Подключение не подтверждено. Проверь настройки и доступ к интернету."; }
            finally { checking = null; busy = false; if (active) UpdateControls(); }
        };
        UpdateControls();
        return panel;
    }
}
