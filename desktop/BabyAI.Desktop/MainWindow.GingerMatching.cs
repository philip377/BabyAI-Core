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
        var source = new TextBlock { Text = "UNIX подготовит вкладку сопоставления и прочитает текущий прайс Ginger. Сопоставление потребует отдельного разрешения.", TextWrapping = TextWrapping.Wrap, FontSize = 15 };
        var candidates = new ComboBox { Header = "Кандидат из каталога Ginger", HorizontalAlignment = HorizontalAlignment.Stretch };
        var candidateFull = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var reason = new TextBox { Header = "Твоё объяснение для обучения", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000, PlaceholderText = "Например: у этого поставщика «немного подмят» означает тестер" };
        var lessonPreview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Header = "Как модель поняла урок и пример применения" };
        var askTeacher = new Button { Content = "Получить предложение модели (урок необязателен)" };
        var score = new ComboBox { Header = "Оценка показанного ответа (0–10)", ItemsSource = Enumerable.Range(0, 11).ToArray(), SelectedIndex = -1 };
        var feedback = new TextBox { Header = "Почему такая оценка / причина отзыва", MaxLength = 4000 };
        var rateAnswer = new Button { Content = "Сохранить оценку ответа", IsEnabled = false };
        var revokeAnswer = new Button { Content = "Отозвать эту оценку", IsEnabled = false };
        JsonElement? ratedReport = null;
        string? answerId = null;
        var ratingBusy = false;
        var confirmLesson = new Button { Content = "Подтверждаю понимание — сохранить урок", IsEnabled = false };
        var loadLesson = new Button { Content = "Показать сохранённый урок" };
        var revokeLesson = new Button { Content = "Отозвать урок" };
        GingerLearning.LanguageLesson? draftLesson = null;
        var teaching = false;
        CancellationTokenSource? teachingCancellation = null;
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
        panel.Children.Add(askTeacher); panel.Children.Add(lessonPreview); panel.Children.Add(confirmLesson);
        panel.Children.Add(score); panel.Children.Add(feedback); panel.Children.Add(rateAnswer); panel.Children.Add(revokeAnswer);
        panel.Children.Add(loadLesson); panel.Children.Add(revokeLesson);
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
            ratedReport = null; answerId = null;
            rateAnswer.IsEnabled = revokeAnswer.IsEnabled = false;
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
            if (_gingerRunning || teaching || ratingBusy || fatal) return;
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
        async Task ReadCurrentPrice(bool prepare = true)
        {
            permit.IsChecked = false;
            await Execute(async token => { observation = await session!.ObserveAsync(token, prepare: prepare); Log("Прайс прочитан. Полные названия доступны выше. " + session.DirectoryPath); });
        }
        reason.TextChanged += (_, _) => { draftLesson = null; confirmLesson.IsEnabled = false; };
        askTeacher.Click += async (_, _) =>
        {
            if (_gingerRunning || teaching || ratingBusy || observation is null || fatal) return;
            var before = observation;
            var text = reason.Text.Trim();
            try
            {
                if (text.Length == 0) text = learning.ReadLanguageLesson(before.Supplier) is { Enabled: true } saved ? saved.Text : "";
            }
            catch (Exception ex) { Log("Не удалось прочитать урок: " + ex.Message); return; }
            teachingCancellation = new CancellationTokenSource();
            ratedReport = null; answerId = null; rateAnswer.IsEnabled = revokeAnswer.IsEnabled = false;
            score.SelectedIndex = -1; feedback.Text = "";
            teaching = true; diagnostics.IsEnabled = false; stop.IsEnabled = true; draftLesson = null; confirmLesson.IsEnabled = false;
            askTeacher.IsEnabled = loadLesson.IsEnabled = revokeLesson.IsEnabled = reason.IsEnabled = false;
            approve.IsChecked = false;
            candidates.SelectedIndex = -1;
            candidates.IsEnabled = approve.IsEnabled = false;
            lessonPreview.Text = text.Length == 0 ? "Модель сравнивает кандидатов по отдельности…" : "Модель разбирает объяснение…";
            try
            {
                var result = await _bridge.PreviewGingerLessonAsync(before.Supplier, before.Title, text,
                    before.Candidates, teachingCancellation.Token);
                teachingCancellation.Token.ThrowIfCancellationRequested();
                var understanding = result.GetProperty("understanding").GetString()!;
                var candidate = result.GetProperty("candidate").GetString();
                lessonPreview.Text = understanding + "\nПример: " + before.Title + "\nКандидат: " + (candidate ?? "нужна ручная проверка")
                    + "\n" + result.GetProperty("explanation").GetString();
                approve.IsChecked = false;
                draftLesson = text.Length == 0 ? null : new(before.Supplier, text, understanding, true, DateTimeOffset.UtcNow);
                confirmLesson.IsEnabled = draftLesson is not null;
                if (result.TryGetProperty("comparisons", out var comparisons))
                {
                    foreach (var comparison in comparisons.EnumerateArray())
                    {
                        var details = comparison.GetProperty("attributes").EnumerateObject().Select(attribute =>
                        {
                            var name = attribute.Name switch { "identity" => "Название", "volume" => "Объём",
                                "concentration" => "Концентрация", "tester" => "Тестер", _ => "Версия/комплектация" };
                            var verdict = attribute.Value.GetString() switch { "match" => "совпадает", "conflict" => "различается", _ => "не определено" };
                            return name + ": " + verdict;
                        });
                        lessonPreview.Text += "\n" + comparison.GetProperty("candidate").GetString() + " — " + string.Join("; ", details);
                    }
                }
                if (candidate is not null && before.Candidates.Count(c => c == candidate) == 1) candidates.SelectedItem = candidate;
                ratedReport = JsonSerializer.SerializeToElement(new {
                    request = new { supplier = before.Supplier, source = before.Title, lesson = text, candidates = before.Candidates },
                    result
                });
                rateAnswer.IsEnabled = true;
                Log("Получено предложение модели. Сопоставление не выполнялось. Проверь понимание и выбранную пару отдельно.");
            }
            catch (OperationCanceledException)
            {
                lessonPreview.Text = "Разбор урока остановлен. Предложение не принято.";
                Log("Разбор урока отменён. Урок не сохранён, кандидат не выбран.");
            }
            catch (Exception ex) { lessonPreview.Text = "Не удалось получить предложение."; Log("Урок не сохранён: " + ex.Message); }
            finally
            {
                teachingCancellation.Dispose(); teachingCancellation = null; teaching = false;
                diagnostics.IsEnabled = true; stop.IsEnabled = false;
                candidates.IsEnabled = approve.IsEnabled = true;
                askTeacher.IsEnabled = loadLesson.IsEnabled = revokeLesson.IsEnabled = reason.IsEnabled = true;
            }
        };
        rateAnswer.Click += async (_, _) =>
        {
            if (ratingBusy || teaching || _gingerRunning || ratedReport is null || answerId is not null) return;
            if (score.SelectedItem is not int points || string.IsNullOrWhiteSpace(feedback.Text))
            { Log("Выбери оценку и напиши пояснение к показанному ответу."); return; }
            ratingBusy = true; rateAnswer.IsEnabled = askTeacher.IsEnabled = false;
            try
            {
                answerId = await _bridge.RateGingerAnswerAsync(ratedReport.Value, points, feedback.Text.Trim());
                revokeAnswer.IsEnabled = true;
                Log($"Оценка ответа {points}/10 сохранена. Веса модели и разрешения Ginger не изменены.");
            }
            catch (Exception ex) { Log("Не удалось сохранить оценку: " + ex.Message); rateAnswer.IsEnabled = true; }
            finally { ratingBusy = false; askTeacher.IsEnabled = true; }
        };
        revokeAnswer.Click += async (_, _) =>
        {
            if (ratingBusy || teaching || _gingerRunning || answerId is null) return;
            if (string.IsNullOrWhiteSpace(feedback.Text)) { Log("Напиши причину отзыва оценки."); return; }
            ratingBusy = true; revokeAnswer.IsEnabled = askTeacher.IsEnabled = false;
            try
            {
                await _bridge.RevokeGingerAnswerAsync(answerId, feedback.Text.Trim());
                Log("Оценка отозвана; исходный ответ сохранён в истории.");
            }
            catch (Exception ex) { Log("Не удалось отозвать оценку: " + ex.Message); revokeAnswer.IsEnabled = true; }
            finally { ratingBusy = false; askTeacher.IsEnabled = true; }
        };
        confirmLesson.Click += (_, _) =>
        {
            if (teaching || _gingerRunning || draftLesson is null || observation?.Supplier != draftLesson.Supplier) return;
            try { learning.SaveLanguageLesson(draftLesson); confirmLesson.IsEnabled = false; permit.IsChecked = false;
                Log("Урок сохранён для этого поставщика. Модель использует его при запросе предложения; автоматическое разрешение не выдано."); }
            catch (Exception ex) { Log("Не удалось сохранить урок: " + ex.Message); }
        };
        loadLesson.Click += (_, _) =>
        {
            if (teaching || ratingBusy || _gingerRunning || observation is null) return;
            ratedReport = null; answerId = null; rateAnswer.IsEnabled = revokeAnswer.IsEnabled = false;
            try { var saved = learning.ReadLanguageLesson(observation.Supplier);
                if (saved is null) { Log("У этого поставщика пока нет урока."); return; }
                reason.Text = saved.Text; lessonPreview.Text = (saved.Enabled ? "Сохранён: " : "Отозван: ") + saved.Understanding;
            } catch (Exception ex) { Log("Не удалось прочитать урок: " + ex.Message); }
        };
        revokeLesson.Click += (_, _) =>
        {
            if (teaching || ratingBusy || _gingerRunning || observation is null) return;
            ratedReport = null; answerId = null; rateAnswer.IsEnabled = revokeAnswer.IsEnabled = false;
            try { var saved = learning.ReadLanguageLesson(observation.Supplier);
                if (saved is null) return;
                learning.SaveLanguageLesson(saved with { Enabled = false, RecordedAt = DateTimeOffset.UtcNow });
                draftLesson = null; confirmLesson.IsEnabled = false; reason.Text = ""; lessonPreview.Text = "Урок отозван.";
                permit.IsChecked = false; Log("Урок отозван. Ранее подтверждённые точные пары сохраняются отдельно.");
            } catch (Exception ex) { Log("Не удалось отозвать урок: " + ex.Message); }
        };
        read.Click += async (_, _) => await ReadCurrentPrice();
        dialog.Opened += async (_, _) =>
        {
            read.Focus(FocusState.Programmatic);
            if (startupReadCheck)
            {
                startupReadCheck = false;
                Log("Диагностический запуск: только чтение текущего прайса. Разрешение сопоставления не выдано.");
                await ReadCurrentPrice(prepare: false);
            }
            else
            {
                Log("Подготавливаю вкладку и панель кандидатов Ginger. Разрешение сопоставления не выдано.");
                await ReadCurrentPrice(prepare: true);
            }
        };
        saveRule.Click += (_, _) =>
        {
            if (_gingerRunning || teaching || fatal || observation is null) return;
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
            string? lessonCandidate = null;
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
                    if (!observation.Linked && session!.HasPriorAttempt(observation))
                    {
                        QueueCurrent();
                        var message = $"Строка {currentPosition}: есть защитная запись прежней попытки. Повтор запрещён; оставлена для разбора.";
                        session.Record(message);
                        Log(message);
                    }
                    else if (!observation.Linked)
                    {
                        observation = await session!.PrepareFiltersAsync(observation, token);
                        var assessment = learning.AssessAutomatic(observation.Supplier, observation.Title, observation.Candidates);
                        var decision = assessment.Decision;
                        if (decision is null) {
                            QueueCurrent();
                            var explanation = $"Строка {currentPosition}: {assessment.Reason}";
                            session.Record(explanation);
                            Log(explanation);
                            var savedLesson = learning.LessonForReview(observation.Supplier, observation.Title);
                            if (savedLesson is not null && observation.Candidates.Count() > 0)
                            {
                                Log("Применяю сохранённый урок к спорной строке. Предложение требует проверки пары.");
                                var proposal = await _bridge.PreviewGingerLessonAsync(observation.Supplier,
                                    observation.Title, savedLesson.Text, observation.Candidates, token);
                                token.ThrowIfCancellationRequested();
                                File.WriteAllText(Path.Combine(session.DirectoryPath, "lesson-review-" + Guid.NewGuid().ToString("N") + ".json"),
                                    JsonSerializer.Serialize(new { lesson = savedLesson, supplier = observation.Supplier,
                                        document = observation.Document, position = observation.Position,
                                        source = observation.Title, observation_directory = observation.Directory, proposal }));
                                lessonPreview.Text = "Пример: " + observation.Title + "\n" +
                                    proposal.GetProperty("understanding").GetString() + "\n" +
                                    proposal.GetProperty("explanation").GetString();
                                lessonCandidate = proposal.GetProperty("candidate").GetString();
                                if (lessonCandidate is not null)
                                {
                                    if (observation.Candidates.Count(c => c == lessonCandidate) != 1)
                                        throw new InvalidDataException("Предложение урока не соответствует единственному кандидату.");
                                    reason.Text = savedLesson.Text;
                                    permit.IsChecked = false;
                                    Log("Проход приостановлен на предложении по уроку. Проверь кандидата и отдельно подтверди пару; клика в Ginger не было.");
                                    break;
                                }
                                Log("По сохранённому уроку модель не выбрала кандидата. Позиция остаётся на ручной проверке.");
                            }
                        }
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
                Log($"{(lessonCandidate is null ? "Проход завершён" : "Проход приостановлен для проверки пары")}. На ручной проверке: {pending.Count}. Документ Ginger оставлен открытым.");
                if (session is not null) Log("Результаты: " + session.DirectoryPath);
            });
            if (!fatal && lessonCandidate is not null) candidates.SelectedItem = lessonCandidate;
        };
        stop.Click += (_, _) => { _gingerCancellation?.Cancel(); if (teaching) teachingCancellation?.Cancel(); Log("Останавливаю перед следующим действием. Если сопоставление уже отправлено, сначала проверю его результат."); };
        diagnostics.Click += (_, _) => { diagnosticsRequested = true; dialog.Hide(); };
        dialog.CloseButtonClick += (_, args) => { if (_gingerRunning || teaching || ratingBusy) { args.Cancel = true; Log(ratingBusy ? "Дождись сохранения оценки." : "Нажми «Стоп» и дождись проверки результата."); } };
        try { await dialog.ShowAsync(); }
        finally { session?.Dispose(); _gingerDialogOpen = false; }
        if (diagnosticsRequested) GingerDiagnosticsNavigationButton_Click(sender, e);
    }
}
