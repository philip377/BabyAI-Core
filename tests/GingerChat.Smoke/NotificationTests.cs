using BabyAI.Desktop;
using System.Text.RegularExpressions;

internal static class NotificationTests
{
    private static readonly GingerChatItem Item = new("Supplier", "Document", 1, "Product", false, ["A"]);
    private static readonly GingerChatMatchSnapshot Snapshot = new(10, 20, 30, "100", "", Item);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static async Task Run()
    {
        CheckOutcomeMessages();
        await CheckPreparationOutcomes();
        await CheckMatchOutcomes();
        await CheckNavigationAndPendingConfirmation();
        CheckWindowsSourceContracts();
        Console.WriteLine("Ginger notifications: review versus verified outcomes, cancellation, deferred navigation, preserved confirmation and navigation-only activation contracts passed; Windows UI was not started.");
    }

    private static void CheckOutcomeMessages()
    {
        Check(GingerTaskNotification.ForPreparation(false, false) == GingerNotificationKind.ManualReview,
            "A model proposal without a confirmation card must remain manual review");
        Check(GingerTaskNotification.ForPreparation(false, true) == GingerNotificationKind.PairReady,
            "An actual confirmation card should produce pair-ready notification");
        foreach (var confirmation in new[] { false, true })
            Check(GingerTaskNotification.ForPreparation(true, confirmation) == GingerNotificationKind.AlreadyLinked,
                "Already-linked row must not be presented as a new match");
        foreach (var cancelled in new[] { false, true })
            Check(GingerTaskNotification.ForMatch(true, cancelled) == GingerNotificationKind.Matched,
                "Late cancellation must not hide a verified write");
        Check(GingerTaskNotification.ForMatch(false, true) == GingerNotificationKind.Cancelled,
            "Cancelled unverified operation reported as success");
        Check(GingerTaskNotification.ForMatch(false, false) == GingerNotificationKind.Failed,
            "Unverified operation reported as matched");
        foreach (var kind in Enum.GetValues<GingerNotificationKind>())
        {
            var notification = GingerTaskNotification.Create(kind);
            Check(!string.IsNullOrWhiteSpace(notification.Title) && notification.Title.Length <= 120,
                "Missing or oversized notification title: " + kind);
            Check(!string.IsNullOrWhiteSpace(notification.Body) && notification.Body.Length <= 360,
                "Missing or oversized notification body: " + kind);
        }
        Check(GingerTaskNotification.Create(GingerNotificationKind.PairReady).Body.Contains("ещё не"),
            "Proposal notification implies that matching already happened");
        Check(GingerTaskNotification.Create(GingerNotificationKind.LessonReady).Body.Contains("ещё не сохранён"),
            "Lesson preview notification implies publication");
        try { GingerTaskNotification.Create((GingerNotificationKind)999); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Unknown notification kind accepted");
    }

    private static async Task CheckPreparationOutcomes()
    {
        foreach (var scenario in new[] { "ready", "guard-rejected", "empty", "already-linked", "cancelled", "failed" })
        {
            using var cancellation = new CancellationTokenSource();
            var stages = new List<GingerChatStage>();
            var comparisons = 0;
            var item = Item with { Linked = scenario == "already-linked",
                Candidates = scenario == "empty" ? [] : ["A"] };
            var outcome = GingerNotificationKind.Failed;
            var flow = new GingerChatFlow(
                _ => {
                    if (scenario == "failed") throw new IOException("read failed");
                    if (scenario == "cancelled") cancellation.Cancel();
                    return Task.FromResult(item);
                }, _ => Task.FromResult(item), (_, _) => {
                    comparisons++;
                    return Task.FromResult("An untrusted model proposal");
                }, (stage, _) => stages.Add(stage));
            try
            {
                await flow.RunAsync(cancellation.Token);
                outcome = GingerTaskNotification.ForPreparation(item.Linked, scenario == "ready");
            }
            catch (OperationCanceledException) { outcome = GingerNotificationKind.Cancelled; }
            catch (IOException) { outcome = GingerNotificationKind.Failed; }
            var expected = scenario switch {
                "ready" => GingerNotificationKind.PairReady,
                "already-linked" => GingerNotificationKind.AlreadyLinked,
                "cancelled" => GingerNotificationKind.Cancelled,
                "failed" => GingerNotificationKind.Failed,
                _ => GingerNotificationKind.ManualReview
            };
            Check(outcome == expected && outcome != GingerNotificationKind.Matched,
                "Preparation produced the wrong completion notification: " + scenario);
            Check(comparisons == (scenario is "ready" or "guard-rejected" ? 1 : 0),
                "Preparation used an unavailable observation: " + scenario);
            Check(stages.Last() == (scenario switch {
                "empty" => GingerChatStage.Empty,
                "cancelled" => GingerChatStage.Cancelled,
                "failed" => GingerChatStage.Failed,
                _ => GingerChatStage.Review
            }), "Notification disagrees with actual flow stage: " + scenario);
        }
    }

    private static async Task CheckMatchOutcomes()
    {
        foreach (var scenario in new[] { "verified", "late-stop", "cancel-before-write", "read-failed", "unverified", "write-failed" })
        {
            using var cancellation = new CancellationTokenSource();
            var verified = false;
            var cancelled = false;
            var writes = 0;
            var gate = new GingerChatMatchGate(Snapshot, "chat-1", "A");
            try
            {
                await gate.RunAsync("chat-1", _ => {
                    if (scenario == "read-failed") throw new IOException("read failed");
                    if (scenario == "cancel-before-write") cancellation.Cancel();
                    return Task.FromResult(Snapshot);
                }, () => false, _ => {
                    writes++;
                    if (scenario == "write-failed") throw new IOException("dispatch uncertain");
                    if (scenario == "late-stop") cancellation.Cancel();
                    return Task.FromResult(scenario == "unverified" ? Snapshot
                        : Snapshot with { LinkedProduct = "A", Item = Item with { Linked = true } });
                }, (stage, _) => verified |= stage == GingerChatMatchStage.Verified, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception error) when (error is IOException or InvalidDataException) { }
            var kind = GingerTaskNotification.ForMatch(verified, cancelled || cancellation.IsCancellationRequested);
            var expected = scenario switch {
                "verified" or "late-stop" => GingerNotificationKind.Matched,
                "cancel-before-write" => GingerNotificationKind.Cancelled,
                _ => GingerNotificationKind.Failed
            };
            Check(kind == expected, "Match notification contradicts verified result: " + scenario);
            Check(writes == (scenario is "cancel-before-write" or "read-failed" ? 0 : 1),
                "Unexpected fake writer dispatch count: " + scenario);
        }
    }

    private static async Task CheckNavigationAndPendingConfirmation()
    {
        foreach (var (busy, running) in new[] { (true, false), (false, true), (true, true) })
        foreach (var blocked in new[] { false, true })
        foreach (var target in new string?[] { null, "", "chat-1", "chat-2" })
        {
            var action = GingerNotificationNavigation.Decide(busy, running, blocked, "chat-1", target);
            Check(action == GingerNotificationNavigationAction.Defer,
                "Notification revealed an overlay while the driver or app was busy");
        }
        foreach (var target in new string?[] { null, "", "chat-1" })
            Check(GingerNotificationNavigation.Decide(false, false, false, "chat-1", target)
                == GingerNotificationNavigationAction.Reveal, "Current-chat notification requests a reload");
        foreach (var target in new string?[] { null, "", "chat-1", "chat-2" })
            Check(GingerNotificationNavigation.Decide(false, false, true, "chat-1", target)
                == GingerNotificationNavigationAction.Reveal, "Notification changed chats with a dialog or approval pending");
        Check(GingerNotificationNavigation.Decide(false, false, false, "chat-1", "chat-2")
            == GingerNotificationNavigationAction.SwitchChat, "An idle notification lost its other-chat target");
        Check(GingerNotificationNavigation.Decide(false, false, false, null, null)
            == GingerNotificationNavigationAction.Reveal, "Unscoped notification should only reveal the app");
        Check(GingerNotificationNavigation.Decide(false, false, false, null, "chat-1")
            == GingerNotificationNavigationAction.SwitchChat, "Cold notification lost its chat target");

        // This gate represents the live card. Navigation decisions cannot consume
        // it; only an explicit later confirmation can dispatch the fake writer.
        var pending = new GingerChatMatchGate(Snapshot, "chat-1", "A");
        var writes = 0;
        for (var repeat = 0; repeat < 2; repeat++)
            Check(GingerNotificationNavigation.Decide(false, false, false, "chat-1", "chat-1")
                == GingerNotificationNavigationAction.Reveal, "Same-chat notification requests a destructive reload");
        Check(writes == 0, "Notification navigation performed a match");
        await pending.RunAsync("chat-1", _ => Task.FromResult(Snapshot), () => false, _ => {
            writes++;
            return Task.FromResult(Snapshot with { LinkedProduct = "A", Item = Item with { Linked = true } });
        }, (_, _) => { }, CancellationToken.None);
        Check(writes == 1, "Revealing a notification discarded the pending confirmation");
    }

    private static string Source(string file) => File.ReadAllText(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../desktop/BabyAI.Desktop", file))).Replace("\r\n", "\n");

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check(start >= 0, "Missing notification integration method: " + signature);
        start = source.IndexOf('{', start);
        var depth = 0;
        for (var position = start; position < source.Length; position++)
        {
            if (source[position] == '{') depth++;
            else if (source[position] == '}' && --depth == 0) return source[start..(position + 1)];
        }
        throw new Exception("Unclosed notification integration method: " + signature);
    }

    private static void Before(string source, string first, string second, string message)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Check(firstIndex >= 0 && secondIndex > firstIndex, message);
    }

    private static void CheckWindowsSourceContracts()
    {
        // Windows callbacks cannot run in this portable smoke harness. These
        // narrow source contracts cover the adapter around the tested pure logic.
        var navigation = Method(Source("MainWindow.GingerNotifications.cs"),
            "private async Task TryOpenGingerNotificationAsync()");
        Before(navigation, "GingerNotificationNavigationAction.Defer) return;", "presenter.Restore(false)",
            "Notification restores an overlay before the Ginger driver is idle");
        Before(navigation, "GingerNotificationNavigationAction.Defer) return;", "ShowFromTray()",
            "Notification brings the app forward while Ginger is working");
        Check(Regex.IsMatch(navigation, @"if\s*\(action\s*==\s*GingerNotificationNavigationAction\.SwitchChat\)\s*await\s+SwitchNavigationAsync\(""chat.select"",\s*chatId\);"),
            "Same-chat reveal can fall through to transcript reload and lose its confirmation");
        Check(Regex.IsMatch(navigation, @"SwitchNavigationAsync\(""chat.select"",\s*chatId\);\s*else\s+if\s*\(!_navigationReady\)"),
            "Initial status load is not restricted to an uninitialized reveal");
        var initialLoad = Method(navigation, "else if (!_navigationReady)");
        Before(initialLoad, "SetBusy(true)", "await RefreshStatusAsync()",
            "Cold notification loads navigation without blocking concurrent actions");
        Check(Regex.IsMatch(initialLoad, @"try\s*\{\s*await\s+RefreshStatusAsync\(\);\s*\}\s*finally\s*\{\s*SetBusy\(false\);\s*\}"),
            "Cold notification does not release busy state after an initial-load failure");
        Check(navigation.Split("RefreshStatusAsync()", StringSplitOptions.None).Length == 2,
            "Warm same-chat reveal may reload status and discard its live confirmation");
        Check(Method(Source("MainWindow.xaml.cs"), "private async Task RefreshStatusAsync()")
            .Contains("await RefreshNavigationAsync()"), "Cold notification refresh no longer loads chat history");
        foreach (var forbidden in new[] { "InvalidateGingerChat", "RefreshNavigationAsync", "GingerChatConfirm_Click",
            ".Gate.RunAsync", ".MatchAsync", "RunGingerFromChatAsync", "ApproveTool" })
            Check(!navigation.Contains(forbidden, StringComparison.Ordinal),
                "Notification activation resets confirmation or executes an action: " + forbidden);

        var application = Method(Source("App.xaml.cs"), "private void OpenNotificationChat(string? chatId)");
        Check(application.Contains("mainWindow.OpenGingerNotificationChat(chatId)"), "App lost the deferred notification route");
        Check(!application.Contains("ShowFromTray") && !application.Contains("SwitchNavigationAsync"),
            "App activation bypasses the driver-idle and pending-confirmation checks");

        var preparation = Method(Source("MainWindow.GingerChat.cs"),
            "private async Task RunGingerFromChatAsync(string message, string? expectedChatId = null)");
        Check(Regex.IsMatch(preparation, @"GingerTaskNotification\.ForPreparation\(\s*observation\?\.Linked\s*==\s*true,\s*_gingerChatConfirmation\s+is\s+not\s+null\)"),
            "Preparation notification trusts a model proposal instead of the actual confirmation card");
        Before(preparation, "OfferGingerChatMatch(", "GingerTaskNotification.ForPreparation(",
            "Notification classifies a pair before the confirmation guards have run");
        Before(preparation, "SetBusy(false)", "NotifyGingerOutcome(notificationKind, chatId)",
            "Preparation notification is published before UI completion");
        var confirmation = Method(Source("MainWindow.GingerChatMatch.cs"),
            "private async void GingerChatConfirm_Click(object sender, RoutedEventArgs e)");
        Check(Regex.IsMatch(confirmation, @"verified\s*=\s*stage\s*==\s*GingerChatMatchStage\.Verified;")
            && Regex.IsMatch(confirmation, @"if\s*\(verified\)\s*matchVerified\s*=\s*true;"),
            "Confirmed notification no longer depends on the gate's verified observation");
        Check(confirmation.Contains("GingerTaskNotification.ForMatch(matchVerified, matchCancelled)"),
            "Late cancellation or history failure can override the verified notification");
        Check(confirmation.Split("NotifyGingerOutcome(", StringSplitOptions.None).Length == 2,
            "One confirmation operation publishes duplicate completion notifications");

        var service = Source("WindowsAppNotifications.cs");
        var callback = Method(service, "private void HandleNotification(AppNotificationActivatedEventArgs args)");
        Before(callback, "action != OpenChatAction", "QueueOpenChat(chatId)", "Activation accepted arbitrary commands");
        Before(callback, "Guid.TryParseExact", "QueueOpenChat(chatId)", "Activation accepted a malformed identity");
        Before(callback, "!IsChatId(chatId)", "QueueOpenChat(chatId)", "Activation accepted an unbounded or executable chat target");
        Before(callback, ".ToString(\"N\")", "_handled.Add", "GUID casing bypasses notification duplicate suppression");
        Before(callback, "!_handled.Add", "QueueOpenChat(chatId)", "Duplicate notification activation can navigate twice");
        Check(callback.Contains("_handledOrder.Count > 128"), "Notification identity history is unbounded");
        Check(!callback.Contains("NavigationAsync") && !callback.Contains("GingerChatConfirm") && !callback.Contains("MatchAsync"),
            "Native activation callback interprets arguments as an executable Ginger action");
        Check(service.Contains("value.Length <= 128") && service.Contains("char.IsAsciiLetterOrDigit(c) || c is '-' or '_'"),
            "Chat activation target validation no longer restricts input to bounded identifiers");
        Before(service, "_manager.NotificationInvoked +=", "_manager.Register()",
            "Cold registration precedes the notification callback subscription");
        var publish = Method(service, "internal void Publish(string title, string body, string? chatId, string taskId)");
        Check(!publish.Contains("AddButton"), "Notification gained an action button outside navigation-only scope");
    }
}
