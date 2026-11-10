using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _gingerStartupReadConsumed;
    private async void GingerNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gingerDialogOpen || _busy || _gingerRunning) return;
        _gingerDialogOpen = true;
        GingerDesktopSession? session = null;
        GingerDesktopObservation? observation = null;
        var learning = new GingerLearning();
        var pending = new List<GingerDesktopObservation>();
        var fatal = false;
        var diagnosticsRequested = false;
        var startupReadCheck = !_gingerStartupReadConsumed && Environment.GetCommandLineArgs().Contains("--ginger-read-check", StringComparer.Ordinal);
        _gingerStartupReadConsumed = true;
        var source = new TextBlock { Text = "Открой прайс Ginger на вкладке «Сопоставление — новые». Агент прочитает полные названия товаров.", TextWrapping = TextWrapping.Wrap, FontSize = 15 };
        var candidates = new ComboBox { Header = "Кандидат из каталога Ginger", HorizontalAlignment = HorizontalAlignment.Stretch };
        var candidateFull = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var reason = new TextBox { Header = "Твоё объяснение для обучения", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000, PlaceholderText = "Например: у этого поставщика «немного подмят» означает тестер" };
        var remember = new CheckBox { Content = "Разрешить эту точную пару в следующих проходах у этого поставщика" };
        var conditionRule = new CheckBox { Content = "Правило поставщика: «немного подмят» означает тестер" };
        var saveRule = new Button { Content = "Сохранить или отозвать правило поставщика", IsEnabled = false };
        var permit = new CheckBox { Content = "Разрешаю сопоставлять однозначные и обученные пары в этом открытом прайсе" };
        var approve = new CheckBox { Content = "Проверил исходный товар и выбранного кандидата — подтверждаю сопоставление" };
        var read = new Button { Content = "Прочитать прайс" };
        var run = new Button { Content = "Автоматический проход", IsEnabled = false };
        var match = new Button { Content = "Сопоставить и запомнить решение", IsEnabled = false };
        var skip = new Button { Content = "На ручную проверку → дальше", IsEnabled = false };
        var stop = new Button { Content = "Стоп", IsEnabled = false };
        var queue = new ComboBox { Header = "Отложенные позиции", DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        var review = new Button { Content = "Открыть выбранную позицию", IsEnabled = false };
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130 };
        var diagnostics = new Button { Content = "Диагностика чтения и OCR" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 550 };
        panel.Children.Add(source);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(read); row.Children.Add(run); row.Children.Add(stop); panel.Children.Add(row);
        panel.Children.Add(permit); panel.Children.Add(candidates); panel.Children.Add(candidateFull); panel.Children.Add(approve);
        panel.Children.Add(reason); panel.Children.Add(remember); panel.Children.Add(match); panel.Children.Add(skip);
        panel.Children.Add(conditionRule); panel.Children.Add(saveRule);
        panel.Children.Add(queue); panel.Children.Add(review); panel.Children.Add(log); panel.Children.Add(diagnostics);
        var dialog = new ContentDialog { Title = "Ginger · Сопоставление и обучение", XamlRoot = Root.XamlRoot,
            Content = new ScrollViewer { Content = panel, MaxHeight = 630 }, CloseButtonText = "Закрыть" };
        void Log(string text)
        {
            log.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
            if (log.Text.Length > 18000) log.Text = log.Text[^18000..];
            SaveGingerLogLine(text);
        }
        void Update()
        {
            if (observation is not null)
            {
                source.Text = $"{observation.Supplier} · документ {observation.Document} · строка {observation.Position}/{observation.Count}\n{observation.Title}\nТовар: {observation.Source.GetProperty("LinkedProductText").GetString()}";
                candidates.ItemsSource = observation.Candidates;
                candidates.SelectedIndex = -1;
            }
            approve.IsChecked = false;
            queue.ItemsSource = pending.ToArray();
            read.IsEnabled = !_gingerRunning && !fatal;
            run.IsEnabled = skip.IsEnabled = review.IsEnabled = observation is not null && !_gingerRunning && !fatal;
            match.IsEnabled = observation is { Linked: false } && !_gingerRunning && !fatal;
            stop.IsEnabled = _gingerRunning;
            diagnostics.IsEnabled = !_gingerRunning;
            saveRule.IsEnabled = conditionRule.IsEnabled = observation is not null && !_gingerRunning && !fatal;
            if (observation is not null) conditionRule.IsChecked = learning.HasDentedTesterRule(observation.Supplier);
            permit.IsEnabled = approve.IsEnabled = candidates.IsEnabled = reason.IsEnabled = remember.IsEnabled = !_gingerRunning;
        }
        void QueueCurrent()
        {
            if (observation is null || observation.Linked) return;
            pending.RemoveAll(p => p.Position == observation.Position);
            pending.Add(observation);
            File.WriteAllText(Path.Combine(session!.DirectoryPath, "manual-review.json"), JsonSerializer.Serialize(pending.Select(p => p.Data).ToArray()));
            Log($"Строка {observation.Position} оставлена для ручной проверки. Сопоставление не выполнялось.");
        }
        async Task Execute(Func<CancellationToken, Task> action)
        {
            if (_gingerRunning || fatal) return;
            _gingerRunning = true;
            _gingerCancellation = new CancellationTokenSource();
            Update(); SetGingerCaptureMode(true); SetGingerPhase(GingerCompanionPhase.Reading);
            try
            {
                session ??= new GingerDesktopSession();
                await action(_gingerCancellation.Token);
                SetGingerPhase(GingerCompanionPhase.Completed);
            }
            catch (OperationCanceledException) { Log("Остановлено. Уже отправленные действия не повторяются."); SetGingerPhase(GingerCompanionPhase.Cancelled); }
            catch (Exception ex)
            {
                fatal = true;
                Log("Проход остановлен. Требуется проверка наблюдений; повтор действия не выполнен. " + ex.Message);
                if (session is not null) Log("Журнал и снимки данных: " + session.DirectoryPath);
                StartupDiagnostics.Log("Ginger matching session failed", ex);
                SetGingerPhase(GingerCompanionPhase.Failed);
            }
            finally
            {
                SetGingerCaptureMode(false); _gingerRunning = false;
                _gingerCancellation.Dispose(); _gingerCancellation = null; Update();
            }
        }
        async Task Next(CancellationToken token)
        {
            if (observation!.Position >= observation.Count) { Log("Достигнут конец прайса."); return; }
            observation = await session!.ObserveAsync(token, observation, observation.Position + 1);
        }
        async Task<GingerDesktopObservation> MatchWithoutOverlay(GingerDesktopObservation before, string candidate, CancellationToken token)
        {
            // A modal UNIX dialog must not cover or reclaim focus from Ginger while
            // the driver verifies selection and sends its single guarded double click.
            var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter
                ?? throw new InvalidOperationException("Не удалось убрать окно UNIX перед сопоставлением.");
            Log("На время сопоставления сворачиваю UNIX. Дождись проверки результата, не переключая окна.");
            presenter.Minimize();
            try { return await session!.MatchAsync(before, candidate, token); }
            finally { presenter.Restore(false); }
        }
        async Task ReadCurrentPrice()
        {
            permit.IsChecked = false;
            await Execute(async token => { observation = await session!.ObserveAsync(token); Log("Прайс прочитан. Полные названия доступны выше. " + session.DirectoryPath); });
        }
        read.Click += async (_, _) => await ReadCurrentPrice();
        dialog.Opened += async (_, _) =>
        {
            read.Focus(FocusState.Programmatic);
            if (startupReadCheck)
            {
                startupReadCheck = false;
                Log("Диагностический запуск: только чтение текущего прайса. Разрешение сопоставления не выдано.");
                await ReadCurrentPrice();
            }
        };
        saveRule.Click += (_, _) =>
        {
            if (_gingerRunning || fatal || observation is null) return;
            try
            {
                var enabled = conditionRule.IsChecked == true;
                learning.TeachDentedTesterRule(observation.Supplier, enabled);
                permit.IsChecked = false;
                Log($"{observation.Supplier}: правило «немного подмят → тестер» " + (enabled ? "сохранено. Остальные признаки товара должны совпадать." : "отозвано."));
            }
            catch (Exception ex) { Log("Не удалось сохранить правило: " + ex.Message); }
        };
        candidates.SelectionChanged += (_, _) => { approve.IsChecked = false; candidateFull.Text = candidates.SelectedItem as string ?? ""; };
        match.Click += async (_, _) =>
        {
            if (observation is null || candidates.SelectedItem is not string chosen || approve.IsChecked != true)
            { Log("Выбери кандидата и подтверди показанную пару."); return; }
            var before = observation; var explanation = reason.Text; var allow = remember.IsChecked == true;
            await Execute(async token =>
            {
                session!.Record("Режим: ручное подтверждение выбранной пары пользователем.");
                observation = await MatchWithoutOverlay(before, chosen, token);
                learning.Teach(new(before.Supplier, before.Title, chosen, explanation, allow, DateTimeOffset.UtcNow));
                pending.RemoveAll(p => p.Position == before.Position);
                Log("Сопоставление проверено в Ginger. Решение сохранено" + (allow ? " для повторного применения." : " без разрешения автоматического повторения."));
            });
        };
        skip.Click += async (_, _) => await Execute(async token => { QueueCurrent(); await Next(token); });
        review.Click += async (_, _) =>
        {
            if (queue.SelectedItem is not GingerDesktopObservation chosen) return;
            await Execute(async token => observation = await session!.ObserveAsync(token, observation, chosen.Position));
        };
        run.Click += async (_, _) =>
        {
            if (permit.IsChecked != true || observation is null) { Log("Нужно разрешение на показанный прайс."); return; }
            var authorizedSupplier = observation.Supplier; var authorizedDocument = observation.Document;
            await Execute(async token =>
            {
                var visited = new HashSet<int>();
                while (observation is not null)
                {
                    token.ThrowIfCancellationRequested();
                    if (observation.Supplier != authorizedSupplier || observation.Document != authorizedDocument)
                        throw new InvalidDataException("Открыт другой прайс; разрешение не распространяется на него.");
                    if (!visited.Add(observation.Position)) throw new InvalidDataException("Повтор строки запрещён.");
                    var currentPosition = observation.Position;
                    if (!observation.Linked)
                    {
                        observation = await session!.PrepareFiltersAsync(observation, token);
                        var decision = learning.DecideAutomatic(observation.Supplier, observation.Title, observation.Candidates);
                        if (decision is null) QueueCurrent();
                        else
                        {
                            session!.Record("Режим: автоматический выбор точной или обученной пары в разрешённом прайсе.");
                            File.WriteAllText(Path.Combine(session.DirectoryPath, "decision-" + Guid.NewGuid().ToString("N") + ".json"),
                                JsonSerializer.Serialize(new { supplier = observation.Supplier, document = observation.Document,
                                    position = observation.Position, source = observation.Title, observation_directory = observation.Directory, decision }));
                            session.Record("Основание выбора: " + decision.Explanation);
                            Log("Основание выбора: " + decision.Explanation);
                            observation = await MatchWithoutOverlay(observation, decision.Candidate, token);
                            Log($"Строка {currentPosition}: автоматическое сопоставление проверено.");
                        }
                    }
                    else Log($"Строка {currentPosition} уже сопоставлена; пропускаю.");
                    if (currentPosition >= observation.Count) break;
                    if (observation.Position == currentPosition) await Next(token);
                    else if (observation.Position != currentPosition + 1) throw new InvalidDataException("После действия выделение переместилось неожиданно.");
                }
                Log($"Проход завершён. На ручной проверке: {pending.Count}. Документ Ginger оставлен открытым.");
                if (session is not null) Log("Результаты: " + session.DirectoryPath);
            });
        };
        stop.Click += (_, _) => { _gingerCancellation?.Cancel(); Log("Останавливаю перед следующим действием. Если сопоставление уже отправлено, сначала проверю его результат."); };
        diagnostics.Click += (_, _) => { diagnosticsRequested = true; dialog.Hide(); };
        dialog.CloseButtonClick += (_, args) => { if (_gingerRunning) { args.Cancel = true; Log("Нажми «Стоп» и дождись проверки результата."); } };
        try { await dialog.ShowAsync(); }
        finally { session?.Dispose(); _gingerDialogOpen = false; }
        if (diagnosticsRequested) GingerDiagnosticsNavigationButton_Click(sender, e);
    }
}
