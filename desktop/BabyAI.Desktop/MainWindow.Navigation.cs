using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private string? _selectedChatId;
    private bool _navigationReady;
    private readonly Dictionary<string, string> _chatDrafts = new();

    private async Task RefreshNavigationAsync(bool restoreTranscript = false)
    {
        var workspaces = await _bridge.NavigationAsync("workspace.list");
        var chats = await _bridge.NavigationAsync("chat.current");
        var activeWorkspace = workspaces.GetProperty("active_id").GetString();
        WorkspaceItems.Children.Clear();
        WorkspaceItems.Children.Add(NavigationItem("Без проекта", "", activeWorkspace is null, true));
        foreach (var item in workspaces.GetProperty("workspaces").EnumerateArray())
            WorkspaceItems.Children.Add(NavigationItem(item.GetProperty("name").GetString()!,
                item.GetProperty("id").GetString()!, item.GetProperty("id").GetString() == activeWorkspace, true));

        var activeChat = chats.GetProperty("active_chat_id").GetString();
        RecentChatItems.Children.Clear();
        foreach (var item in chats.GetProperty("chats").EnumerateArray())
            RecentChatItems.Children.Add(NavigationItem(item.GetProperty("title").GetString()!,
                item.GetProperty("id").GetString()!, item.GetProperty("id").GetString() == activeChat, false));
        HistoryModeText.Text = chats.GetProperty("history_enabled").GetBoolean()
            ? "История сохраняется" : "История выключена · тексты только до перезапуска";
        if (restoreTranscript || !_navigationReady || activeChat != _selectedChatId)
        {
            _conversation.Clear();
            foreach (var item in chats.GetProperty("messages").EnumerateArray())
            {
                var speaker = item.GetProperty("role").GetString() == "user" ? "Вы" : "BabyAI";
                _conversation.Add($"{speaker}: {item.GetProperty("content").GetString()}");
            }
            RenderConversation();
            MessageBox.Text = activeChat is not null && _chatDrafts.TryGetValue(activeChat, out var draft) ? draft : "";
        }
        _selectedChatId = activeChat;
        _navigationReady = true;
        await RefreshJobsAsync();
    }

    private Button NavigationItem(string title, string id, bool selected, bool workspace)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11 };
        row.Children.Add(new FontIcon { Glyph = workspace ? "\uE8B7" : "\uE8A5", FontSize = 16, Width = 24 });
        row.Children.Add(new TextBlock { Text = title, FontSize = 12, MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = _navigationExpanded ? Visibility.Visible : Visibility.Collapsed });
        var button = new Button { Content = row, Style = (Style)Root.Resources["NavigationButtonStyle"] };
        if (selected)
            button.Background = new SolidColorBrush(Color.FromArgb(36, 130, 147, 218));
        ToolTipService.SetToolTip(button, title);
        AutomationProperties.SetName(button, selected ? $"{title}, выбран" : title);
        button.Click += async (_, _) => await SwitchNavigationAsync(
            workspace ? (id.Length == 0 ? "workspace.clear_active" : "workspace.select") : "chat.select", id);
        return button;
    }

    private async Task SwitchNavigationAsync(string command, string? id = null)
    {
        if (_busy) return;
        if (ApprovalCard.Visibility == Visibility.Visible)
        {
            ReplyText.Text = "Сначала подтвердите или отклоните ожидающее действие.";
            return;
        }
        try
        {
            SetBusy(true);
            if (_selectedChatId is not null) _chatDrafts[_selectedChatId] = MessageBox.Text;
            await _bridge.NavigationAsync(command, id);
            await RefreshNavigationAsync(restoreTranscript: true);
            await RefreshStatusAsync();
            ReplyText.Text = "Чат готов.";
        }
        catch (Exception ex) { ShowBridgeError(ex); }
        finally { SetBusy(false); }
    }

    private void UpdateNavigationItemLabels()
    {
        foreach (var panel in new[] { WorkspaceItems, RecentChatItems })
            foreach (var child in panel.Children)
                if (child is Button { Content: StackPanel row } && row.Children.Count > 1)
                    row.Children[1].Visibility = _navigationExpanded ? Visibility.Visible : Visibility.Collapsed;
        HistoryModeText.Visibility = _navigationExpanded ? Visibility.Visible : Visibility.Collapsed;
    }
}