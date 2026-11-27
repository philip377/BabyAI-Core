using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _notificationOpenTimer;
    private bool _notificationOpenPending;
    private bool _notificationOpening;
    private string? _notificationChatId;

    private void NotifyGingerOutcome(GingerNotificationKind kind, string? chatId = null)
    {
        // Notification text is fixed: supplier names, prices and errors stay in the app.
        // This is best-effort presentation and must never change a Ginger outcome.
        try
        {
            var notification = GingerTaskNotification.Create(kind);
            ((App)Application.Current).GingerNotifications.Publish(notification.Title, notification.Body,
                chatId, Guid.NewGuid().ToString("N")[..16]);
        }
        catch (Exception ex) { StartupDiagnostics.Log("Ginger notification unavailable", ex); }
    }

    internal void OpenGingerNotificationChat(string? chatId)
    {
        if (_exitRequested) return;
        _notificationChatId = chatId;
        _notificationOpenPending = true;
        _notificationOpenTimer ??= CreateNotificationOpenTimer();
        _notificationOpenTimer.Start();
        _ = TryOpenGingerNotificationAsync();
    }

    private DispatcherQueueTimer CreateNotificationOpenTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += async (_, _) => await TryOpenGingerNotificationAsync();
        Closed += (_, _) => timer.Stop();
        return timer;
    }

    private async Task TryOpenGingerNotificationAsync()
    {
        if (_notificationOpening) return;
        if (!_notificationOpenPending || _exitRequested)
        {
            _notificationOpenTimer?.Stop();
            return;
        }
        var action = GingerNotificationNavigation.Decide(_busy, _gingerRunning,
            _gingerDialogOpen || _agentsDialogOpen || ApprovalCard.Visibility == Visibility.Visible,
            _selectedChatId, _notificationChatId);
        if (action == GingerNotificationNavigationAction.Defer) return;
        var chatId = _notificationChatId;
        _notificationOpenPending = false;
        _notificationOpenTimer?.Stop();
        _notificationOpening = true;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore(false);
            ShowFromTray();
            if (!_expanded)
            {
                _expanded = true;
                await ExpandPanelAsync();
            }
            // Same-chat navigation must not reload history and discard the confirmation card.
            // A notification is only a navigation request: never invoke a Ginger handler here.
            if (action == GingerNotificationNavigationAction.SwitchChat)
                await SwitchNavigationAsync("chat.select", chatId);
            else if (!_navigationReady)
            {
                // A cold, unscoped activation still needs the first status/history load.
                // Do not run this for an already-open chat: its live card is authoritative.
                SetBusy(true);
                try { await RefreshStatusAsync(); }
                finally { SetBusy(false); }
            }
            ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null);
        }
        catch (Exception ex) { StartupDiagnostics.Log("Opening Ginger notification failed", ex); }
        finally { _notificationOpening = false; }
    }
}
