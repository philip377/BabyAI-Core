using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BabyAI.Desktop;

internal sealed record GingerTableSelection(long Window, string Status, int? ItemCount = null,
    int? SelectedCount = null, int? SelectedIndex = null)
{
    // A row index changes on sort/filter; it is never a supplier or catalogue ID.
    public bool IdentityVerified => false;
}

internal sealed record GingerSelectionSnapshot(string Status, int? ProcessId,
    long? Window, bool Foreground, IReadOnlyList<GingerTableSelection> Tables)
{
    public bool ReadOnly => true;
    public bool CanWrite => false;
}

internal static class GingerNativeSelection
{
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam,
        IntPtr lparam, uint flags, uint timeout, out IntPtr result);

    internal static GingerSelectionSnapshot Inspect()
    {
        var processes = Process.GetProcessesByName("Ginger4");
        try
        {
            var visible = processes.Where(p => p.MainWindowHandle != IntPtr.Zero).ToArray();
            if (visible.Length != 1)
                return new(visible.Length == 0 ? "not_open" : "multiple_windows", null, null, false, []);
            var process = visible[0];
            var root = process.MainWindowHandle;
            var tables = new List<GingerTableSelection>();
            var visited = 0;
            var truncated = false;
            EnumProc callback = (window, _) =>
            {
                if (++visited > 600) { truncated = true; return false; }
                var name = new StringBuilder(256);
                if (GetClassNameW(window, name, name.Capacity) > 0 &&
                    System.Text.RegularExpressions.Regex.IsMatch(name.ToString(), @"(^|[.])SysListView32([.]|$)"))
                {
                    // Independent table cap bounds total native-message time.
                    if (tables.Count >= 12) { truncated = true; return false; }
                    tables.Add(Read(window, (uint)process.Id));
                }
                return true;
            };
            EnumChildWindows(root, callback, IntPtr.Zero);
            GetWindowThreadProcessId(root, out var owner);
            if (owner != process.Id || process.HasExited)
                return new("window_changed", null, null, false, []);
            return new(truncated ? "truncated" : "observed", process.Id, root.ToInt64(),
                GetForegroundWindow() == root, tables);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static GingerTableSelection Read(IntPtr window, uint expectedProcess)
    {
        GingerTableSelection Unavailable() => new(window.ToInt64(), "unavailable");
        bool OwnsWindow()
        {
            GetWindowThreadProcessId(window, out var owner);
            var name = new StringBuilder(256);
            return owner != 0 && owner == expectedProcess &&
                GetClassNameW(window, name, name.Capacity) > 0 &&
                System.Text.RegularExpressions.Regex.IsMatch(name.ToString(), @"(^|[.])SysListView32([.]|$)");
        }
        int? Query(uint message, int wparam = 0, int lparam = 0)
        {
            if (!OwnsWindow()) return null;
            var ok = SendMessageTimeout(window, message, new IntPtr(wparam), new IntPtr(lparam),
                0x0002, 100, out var value); // SMTO_ABORTIFHUNG; no pointer payloads
            return ok == IntPtr.Zero ? null : unchecked((int)value.ToInt64());
        }
        // Read-only scalar messages: GETITEMCOUNT, GETSELECTEDCOUNT, GETNEXTITEM.
        var items = Query(0x1004);
        var selected = Query(0x1032);
        var index = Query(0x100C, -1, 0x0002); // LVNI_SELECTED
        if (items is null || selected is null || index is null) return Unavailable();
        var itemsAfter = Query(0x1004);
        var selectedAfter = Query(0x1032);
        var indexAfter = Query(0x100C, -1, 0x0002);
        if (itemsAfter is null || selectedAfter is null || indexAfter is null || !OwnsWindow()) return Unavailable();
        if (items != itemsAfter || selected != selectedAfter || index != indexAfter)
            return new(window.ToInt64(), "changed_during_read");
        if (items < 0 || selected < 0 || selected > items ||
            (selected == 0 && index != -1) || (selected > 0 && (index < 0 || index >= items)))
            return Unavailable();
        return new(window.ToInt64(), selected == 0 ? "no_selection" : selected > 1 ? "multiple_selection" : "single_index_observed",
            items, selected, selected == 1 ? index : null);
    }
}
