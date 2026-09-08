using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _jobUiInitialized;

    private async Task RefreshJobsAsync()
    {
        JsonElement response;
        try
        {
            response = await _bridge.NavigationAsync("job.list");
        }
        catch
        {
            // Jobs are an additive surface. A broken job database must not make
            // chat navigation unusable; the existing status path will still show
            // the underlying bridge error when a job action is explicitly used.
            return;
        }

        if (!response.TryGetProperty("jobs", out var jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Array)
            return;

        var jobs = jobsElement.EnumerateArray().Select(item => item.Clone()).ToList();
        var active = jobs.Where(item => !IsTerminalJob(JobStatus(item))).ToList();

        if (!_jobUiInitialized)
        {
            TaskText.Tapped += TaskText_Tapped;
            _jobUiInitialized = true;
        }

        if (active.Count > 0)
        {
            var primary = active[0];
            var primaryGoal = CompactGoal(JobGoal(primary), 72);
            TaskText.Text = active.Count == 1
                ? $"{JobStatusLabel(JobStatus(primary))} · {primaryGoal}"
                : $"{active.Count} активных задач · {primaryGoal}";
            ToolTipService.SetToolTip(TaskText, "Нажмите, чтобы открыть задачи UNIX");
        }
        else if (jobs.Count > 0)
        {
            ToolTipService.SetToolTip(TaskText, "Нажмите, чтобы посмотреть последние задачи UNIX");
        }
        else
        {
            TaskText.ContextFlyout = null;
            ToolTipService.SetToolTip(TaskText, null);
            return;
        }

        TaskText.ContextFlyout = BuildJobsFlyout(jobs.Take(8).ToList());
    }

    private MenuFlyout BuildJobsFlyout(IReadOnlyList<JsonElement> jobs)
    {
        var flyout = new MenuFlyout();
        foreach (var job in jobs)
        {
            var id = JobId(job);
            var status = JobStatus(job);
            var chatId = JobChatId(job);
            var title = $"{JobStatusLabel(status)} · {CompactGoal(JobGoal(job), 48)}";
            var group = new MenuFlyoutSubItem { Text = title };

            if (!string.IsNullOrWhiteSpace(chatId) && chatId != _selectedChatId)
            {
                var openChat = new MenuFlyoutItem { Text = "Открыть исходный чат" };
                openChat.Click += async (_, _) => await SwitchNavigationAsync("chat.select", chatId);
                group.Items.Add(openChat);
            }
            else if (status == "queued")
            {
                var run = new MenuFlyoutItem { Text = "Запустить" };
                run.Click += async (_, _) => await RunJobAsync(id, resumeFirst: false);
                group.Items.Add(run);
            }
            else if (status is "paused" or "failed")
            {
                var resume = new MenuFlyoutItem { Text = "Продолжить" };
                resume.Click += async (_, _) => await RunJobAsync(id, resumeFirst: true);
                group.Items.Add(resume);
            }
            else if (status == "waiting_permission")
            {
                group.Items.Add(new MenuFlyoutItem
                {
                    Text = "Ожидает вашего разрешения",
                    IsEnabled = false,
                });
            }
            else if (status == "running")
            {
                group.Items.Add(new MenuFlyoutItem
                {
                    Text = "Выполняется",
                    IsEnabled = false,
                });
            }

            if (!IsTerminalJob(status))
            {
                var cancel = new MenuFlyoutItem { Text = "Отменить задачу" };
                cancel.Click += async (_, _) => await CancelJobAsync(id);
                group.Items.Add(cancel);
            }

            if (group.Items.Count == 0)
            {
                group.Items.Add(new MenuFlyoutItem
                {
                    Text = JobTerminalDetail(job),
                    IsEnabled = false,
                });
            }
            flyout.Items.Add(group);
        }
        return flyout;
    }

    private void TaskText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TaskText.ContextFlyout is not null)
        {
            TaskText.ContextFlyout.ShowAt(TaskText);
            e.Handled = true;
        }
    }

    private async Task RunJobAsync(string id, bool resumeFirst)
    {
        if (_busy || string.IsNullOrWhiteSpace(id))
            return;
        try
        {
            SetBusy(true);
            ReplyText.Text = resumeFirst ? "Возобновляю задачу…" : "Запускаю задачу…";
            if (resumeFirst)
                await _bridge.NavigationAsync("job.resume", id);
            var result = await _bridge.NavigationAsync("job.run", id);
            if (result.TryGetProperty("reply", out var replyElement))
            {
                var reply = replyElement.GetString();
                if (!string.IsNullOrWhiteSpace(reply))
                    AppendConversation("UNIX", reply);
            }
            await RefreshStatusAsync();
            ReplyText.Text = ApprovalCard.Visibility == Visibility.Visible
                ? "Задача ждёт разрешения."
                : "Задача завершена.";
        }
        catch (Exception ex)
        {
            ShowBridgeError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CancelJobAsync(string id)
    {
        if (_busy || string.IsNullOrWhiteSpace(id))
            return;
        try
        {
            SetBusy(true);
            await _bridge.NavigationAsync("job.cancel", id);
            await RefreshStatusAsync();
            ReplyText.Text = "Задача отменена.";
        }
        catch (Exception ex)
        {
            ShowBridgeError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string JobId(JsonElement job) =>
        job.TryGetProperty("id", out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string JobStatus(JsonElement job) =>
        job.TryGetProperty("status", out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string JobGoal(JsonElement job) =>
        job.TryGetProperty("goal", out var value) ? value.GetString() ?? "Задача" : "Задача";

    private static string? JobChatId(JsonElement job) =>
        job.TryGetProperty("chat_id", out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static bool IsTerminalJob(string status) => status is "completed" or "cancelled";

    private static string JobStatusLabel(string status) => status switch
    {
        "queued" => "В очереди",
        "running" => "Выполняю",
        "waiting_permission" => "Нужно разрешение",
        "paused" => "На паузе",
        "failed" => "Ошибка",
        "completed" => "Готово",
        "cancelled" => "Отменено",
        _ => "Задача",
    };

    private static string JobTerminalDetail(JsonElement job)
    {
        var status = JobStatus(job);
        if (status == "completed")
            return "Завершена";
        if (status == "cancelled")
            return "Отменена";
        return JobStatusLabel(status);
    }

    private static string CompactGoal(string value, int limit)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= limit ? compact : compact[..Math.Max(1, limit - 1)] + "…";
    }
}
