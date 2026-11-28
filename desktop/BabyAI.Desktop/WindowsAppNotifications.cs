using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Win32;

namespace BabyAI.Desktop;

/// <summary>Local Windows notifications whose only activation action is opening a chat.</summary>
internal sealed class WindowsAppNotifications : IDisposable
{
    private const string InstalledAppId = "BabyAI.Desktop";
    private const string OpenChatAction = "open-ginger-chat";
    private const string NotificationLaunchArgument = "----AppNotificationActivated:";
    private readonly object _gate = new();
    private readonly Queue<string?> _pending = new();
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private readonly Queue<string> _handledOrder = new();
    private AppNotificationManager? _manager;
    private AppInstance? _instance;
    private DispatcherQueue? _dispatcher;
    private Action<string?>? _openChat;
    private bool _registered;
    private bool _disposed;
    private bool _isPrimary = true;

    /// <summary>Runs before constructing any window or starting the bridge worker.</summary>
    internal async Task<bool> PrepareLaunchAsync()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        string? appId = null;
        var identityConfigured = false;
        try
        {
            // The SDK's generated unpackaged identity otherwise changes with each version slot.
            appId = GetApplicationId();
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
            identityConfigured = true;
        }
        catch (Exception ex) { LogFailure("identity", ex); }

        try
        {
            _instance = AppInstance.FindOrRegisterForKey("BabyAI.Desktop.Main");
            _isPrimary = _instance.IsCurrent;
            if (_isPrimary) _instance.Activated += OnRedirectedActivation;
        }
        catch (Exception ex) { LogFailure("instance registration", ex); }

        try
        {
            if (AppNotificationManager.IsSupported())
            {
                _manager = AppNotificationManager.Default;
                // Required ordering: otherwise a warm click creates another process.
                _manager.NotificationInvoked += OnNotificationInvoked;
                _manager.Register();
                _registered = true;
                if (_isPrimary && identityConfigured && appId == InstalledAppId) RefreshInstalledActivationPath();
                StartupDiagnostics.Log("Windows app notifications registered");
            }
            else StartupDiagnostics.Log("Windows app notifications unavailable for this process");
        }
        catch (Exception ex) { LogFailure("registration", ex); }

        try
        {
            // Register must precede this call. Deserialization can wait for COM activation;
            // keep the UI apartment free to dispatch it during a cold notification launch.
            var activation = await Task.Run(() => AppInstance.GetCurrent().GetActivatedEventArgs())
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (!_isPrimary && _instance is not null)
            {
                await _instance.RedirectActivationToAsync(activation).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                StartupDiagnostics.Log("Desktop activation redirected to the existing instance");
                return false;
            }
            if (activation.Kind == ExtendedActivationKind.AppNotification &&
                activation.Data is AppNotificationActivatedEventArgs notification)
                HandleNotification(notification);
        }
        catch (Exception ex)
        {
            LogFailure("activation", ex);
            // A failed redirect must not create a second window or bridge worker.
            if (!_isPrimary) return false;
        }
        return true;
    }

    internal void Initialize(Action<string?> openChat)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _openChat = openChat;
        }
        DispatchPending();
    }

    internal void Publish(string title, string body, string? chatId, string taskId)
    {
        try
        {
            if (_disposed || !_registered || !_isPrimary || _manager is null) return;
            if (_manager.Setting != AppNotificationSetting.Enabled)
            {
                StartupDiagnostics.Log("Windows app notification skipped: notifications disabled in Windows");
                return;
            }
            var builder = new AppNotificationBuilder()
                .AddArgument("action", OpenChatAction)
                .AddArgument("notification", Guid.NewGuid().ToString("N"))
                .AddText(CleanText(title, 120))
                .AddText(CleanText(body, 360));
            if (IsChatId(chatId)) builder.AddArgument("chat", chatId!);
            var notification = builder.BuildNotification();
            notification.Group = "ginger";
            notification.Tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))[..16];
            notification.Expiration = DateTimeOffset.Now.AddDays(7);
            _manager.Show(notification);
            StartupDiagnostics.Log(notification.Id != 0
                ? "Ginger notification submitted to Windows"
                : "Windows did not accept the Ginger notification");
        }
        catch (Exception ex) { LogFailure("publish", ex); }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try { if (_isPrimary) HandleNotification(args); }
        catch (Exception ex) { LogFailure("notification callback", ex); }
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        try
        {
            if (args.Kind == ExtendedActivationKind.AppNotification &&
                args.Data is AppNotificationActivatedEventArgs notification)
                HandleNotification(notification);
            else if (args.Kind == ExtendedActivationKind.Launch)
                QueueOpenChat(null);
        }
        catch (Exception ex) { LogFailure("redirect callback", ex); }
    }

    private void HandleNotification(AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;
        // Never interpret notification input as a command, consent, script, or task to run.
        if (!arguments.TryGetValue("action", out var action) || action != OpenChatAction) return;
        if (!arguments.TryGetValue("notification", out var id) || !Guid.TryParseExact(id, "N", out var notificationId)) return;
        id = notificationId.ToString("N");
        arguments.TryGetValue("chat", out var chatId);
        if (chatId is not null && !IsChatId(chatId)) return;
        lock (_gate)
        {
            if (_disposed || !_handled.Add(id)) return;
            _handledOrder.Enqueue(id);
            if (_handledOrder.Count > 128) _handled.Remove(_handledOrder.Dequeue());
        }
        QueueOpenChat(chatId);
    }

    private void QueueOpenChat(string? chatId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.Count >= 16) _pending.Dequeue();
            _pending.Enqueue(chatId);
        }
        DispatchPending();
    }

    private void DispatchPending()
    {
        lock (_gate)
            if (_disposed || _openChat is null || _pending.Count == 0) return;
        if (_dispatcher?.TryEnqueue(() =>
        {
            while (true)
            {
                Action<string?>? callback;
                string? chatId;
                lock (_gate)
                {
                    if (_disposed || _openChat is null || _pending.Count == 0) return;
                    callback = _openChat;
                    chatId = _pending.Dequeue();
                }
                try { callback(chatId); }
                catch (Exception ex) { LogFailure("open chat", ex); }
            }
        }) != true)
            StartupDiagnostics.Log("Windows notification navigation could not reach the UI dispatcher");
    }

    private static bool IsChatId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string CleanText(string value, int limit)
    {
        var clean = new string(value.Where(c => !char.IsControl(c) || c is '\r' or '\n').ToArray()).Trim();
        if (clean.Length <= limit) return clean;
        var length = char.IsHighSurrogate(clean[limit - 1]) ? limit - 1 : limit;
        return clean[..length] + "…";
    }

    private static string GetApplicationId()
    {
        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI");
        var appDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var versionsRoot = Path.Combine(installRoot, "versions");
        if (appDirectory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(appDirectory.Parent?.Parent?.FullName, versionsRoot, StringComparison.OrdinalIgnoreCase))
            return InstalledAppId;
        // Debug builds must not take over the installed app's notification identity or COM entry.
        return InstalledAppId + ".Dev." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant())))[..16];
    }

    private static void RefreshInstalledActivationPath()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI");
            using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "current.json")));
            var selected = Path.GetFullPath(current.RootElement.GetProperty("path").GetString()!);
            var executable = Path.Combine(selected, "app", "BabyAI.Desktop.exe");
            if (!string.Equals(executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            var versionsPrefix = Path.Combine(root, "versions") + Path.DirectorySeparatorChar;
            if (!selected.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase)) return;

            using var identity = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + InstalledAppId);
            var activator = identity?.GetValue("CustomActivator") as string;
            if (!Guid.TryParse(activator, out var clsid)) return;
            using var server = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\CLSID\" + clsid.ToString("B") + @"\LocalServer32", writable: true);
            var existing = server?.GetValue(null) as string;
            if (existing is null || !existing.StartsWith('"')) return;
            var end = existing.IndexOf('"', 1);
            if (end < 1 || existing[(end + 1)..].Trim() != NotificationLaunchArgument) return;
            var oldPath = Path.GetFullPath(existing[1..end]);
            if (!oldPath.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(oldPath).Equals("BabyAI.Desktop.exe", StringComparison.OrdinalIgnoreCase)) return;
            // SDK Register creates this value only for a new CLSID. Refresh our own existing
            // registration after an update so retained notifications launch the selected slot.
            server!.SetValue(null, $"\"{executable}\" {NotificationLaunchArgument}", RegistryValueKind.String);
        }
        catch (Exception ex) { LogFailure("installed activation path", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _openChat = null;
        }
        try { if (_instance is not null && _isPrimary) _instance.Activated -= OnRedirectedActivation; }
        catch (Exception ex) { LogFailure("instance cleanup", ex); }
        try { if (_manager is not null) _manager.NotificationInvoked -= OnNotificationInvoked; }
        catch (Exception ex) { LogFailure("event cleanup", ex); }
        try { if (_registered) _manager?.Unregister(); }
        catch (Exception ex) { LogFailure("unregister", ex); }
        // UnregisterAll would erase retained notifications and cold-start registration.
        try { if (_instance is not null && _isPrimary) _instance.UnregisterKey(); }
        catch (Exception ex) { LogFailure("instance key cleanup", ex); }
    }

    private static void LogFailure(string stage, Exception ex) =>
        StartupDiagnostics.Log($"Windows app notifications: {stage} failed", ex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
