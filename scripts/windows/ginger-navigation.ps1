# Fixed semantic menu navigation. No coordinates, text entry, SQL or catalogue writes.
[CmdletBinding()]
param([ValidateSet('Inspect', 'OpenPrices')][string]$Mode = 'Inspect')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName Accessibility
Add-Type -ReferencedAssemblies @([Accessibility.IAccessible].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

public sealed class GingerMenuNode {
    public IAccessible Owner;
    public object Child;
    public string Name;
    public string Path;
    public int State; public int Role;
}

public static class GingerMenuAccess {
    private delegate bool EnumProc(IntPtr hwnd, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, EnumProc callback, IntPtr unused);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr root);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
    [DllImport("oleacc.dll")] private static extern int WindowFromAccessibleObject(IAccessible accessible, out IntPtr hwnd);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible parent, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=2)] object[] children, out int obtained);

    public static string Normalize(string value) {
        return (value ?? "").Replace("&", "").Trim().ToLowerInvariant();
    }
    public static void RequireWindow(IntPtr root, uint process) {
        uint owner;
        if (root == IntPtr.Zero || process == 0 || GetWindowThreadProcessId(root, out owner) == 0 || owner != process)
            throw new InvalidOperationException("Ginger window changed");
        IntPtr popup = GetLastActivePopup(root);
        if (popup != root && popup != IntPtr.Zero && IsWindowVisible(popup))
            throw new InvalidOperationException("A modal or owned window requires attention");
    }
    private static List<IntPtr> Windows(IntPtr root, uint process) {
        RequireWindow(root, process);
        List<IntPtr> windows = new List<IntPtr>();
        windows.Add(root);
        bool limited = false;
        EnumProc callback = delegate(IntPtr hwnd, IntPtr unused) {
            if (windows.Count >= 600) { limited = true; return false; }
            windows.Add(hwnd); return true;
        };
        EnumChildWindows(root, callback, IntPtr.Zero);
        if (limited) throw new InvalidOperationException("Window enumeration truncated");
        return windows;
    }
    private static IAccessible Client(IntPtr hwnd) {
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IAccessible value;
        return AccessibleObjectFromWindow(hwnd, unchecked((uint)-4), ref iid, out value) >= 0 ? value : null;
    }
    private static void Walk(IAccessible owner, object child, string ancestors, int depth, List<GingerMenuNode> nodes) {
        if (depth > 20 || nodes.Count >= 2000) throw new InvalidOperationException("Accessibility enumeration truncated");
        string name = owner.get_accName(child) ?? "";
        int role = Convert.ToInt32(owner.get_accRole(child));
        int state = Convert.ToInt32(owner.get_accState(child));
        string path = ancestors;
        if (role == 12) path = ancestors.Length == 0 ? Normalize(name) : ancestors + "/" + Normalize(name);
        nodes.Add(new GingerMenuNode { Owner=owner, Child=child, Name=name, Path=path, State=state, Role=role });
        if (!(child is int) || (int)child != 0) return;
        int count = owner.accChildCount;
        if (count < 0 || count > 2000 - nodes.Count) throw new InvalidOperationException("Invalid child count");
        if (count == 0) return;
        object[] children = new object[count]; int obtained;
        if (AccessibleChildren(owner, 0, count, children, out obtained) < 0 || obtained != count)
            throw new InvalidOperationException("Incomplete accessibility read");
        foreach (object item in children) {
            IAccessible nested = item as IAccessible;
            if (nested != null) Walk(nested, 0, path, depth + 1, nodes);
            else if (item is int && (int)item > 0) Walk(owner, item, path, depth + 1, nodes);
            else throw new InvalidOperationException("Unknown accessibility child");
        }
    }
    public static List<GingerMenuNode> Menu(IntPtr root, uint process) {
        List<IAccessible> roots = new List<IAccessible>();
        foreach (IntPtr hwnd in Windows(root, process)) {
            IAccessible value = Client(hwnd);
            if (value != null && Normalize(value.get_accName(0)) == "menustrip1") roots.Add(value);
        }
        if (roots.Count != 1) throw new InvalidOperationException("Expected one menuStrip1");
        List<GingerMenuNode> nodes = new List<GingerMenuNode>();
        Walk(roots[0], 0, "", 0, nodes);
        RequireWindow(root, process);
        return nodes;
    }
    public static void Invoke(IntPtr root, uint process, string path) {
        if (path != "журналы" && path != "журналы/текущие прайс-листы")
            throw new InvalidOperationException("Menu action is not allowed");
        List<GingerMenuNode> matches = Menu(root, process).FindAll(n => n.Role == 12 && n.Path == path && Normalize(n.Name) == path.Substring(path.LastIndexOf('/') + 1));
        if (matches.Count != 1) throw new InvalidOperationException("Menu item is missing or ambiguous");
        GingerMenuNode item = matches[0];
        IntPtr hwnd; uint owner;
        if (WindowFromAccessibleObject(item.Owner, out hwnd) < 0 || GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != process)
            throw new InvalidOperationException("Menu owner changed");
        RequireWindow(root, process);
        int state = Convert.ToInt32(item.Owner.get_accState(item.Child));
        // Unavailable, invisible or offscreen controls cannot be invoked.
        if ((state & (1 | 0x8000 | 0x10000)) != 0 || Normalize(item.Owner.get_accName(item.Child)) != Normalize(item.Name))
            throw new InvalidOperationException("Menu item changed or is unavailable");
        if (String.IsNullOrWhiteSpace(item.Owner.get_accDefaultAction(item.Child)))
            throw new InvalidOperationException("Menu exposes no default action");
        item.Owner.accDoDefaultAction(item.Child);
    }
    public static List<string[]> Headers(IntPtr root, uint process) {
        List<string[]> result = new List<string[]>();
        foreach (IntPtr hwnd in Windows(root, process)) {
            StringBuilder name = new StringBuilder(256);
            GetClassNameW(hwnd, name, name.Capacity);
            if (!name.ToString().Contains("SysHeader32") || !IsWindowVisible(hwnd)) continue;
            IAccessible value = Client(hwnd);
            if (value == null) throw new InvalidOperationException("Header is unreadable");
            List<GingerMenuNode> nodes = new List<GingerMenuNode>();
            Walk(value, 0, "", 0, nodes);
            result.Add(nodes.ConvertAll(n => n.Name).ToArray());
        }
        return result;
    }
    public static bool PricesVisible(IntPtr root, uint process) {
        foreach (string[] header in Headers(root, process)) {
            bool supplier = false, included = false;
            foreach (string name in header) {
                supplier |= Normalize(name) == "поставщик";
                included |= Normalize(name) == "входит в сводные";
            }
            if (supplier && included) return true;
        }
        return false;
    }
}
'@

$processes = @(Get-Process -Name Ginger4 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
if ($processes.Count -ne 1) { throw 'Exactly one visible Ginger4 process is required' }
$target = $processes[0]
if ([IO.Path]::GetFileName($target.Path) -ine 'Ginger4.exe') { throw 'Unexpected process executable' }
$window = $target.MainWindowHandle
$processId = [uint32]$target.Id
$actions = [System.Collections.Generic.List[string]]::new()
if ($Mode -eq 'OpenPrices' -and -not [GingerMenuAccess]::PricesVisible($window, $processId)) {
    [GingerMenuAccess]::Invoke($window, $processId, 'журналы')
    $actions.Add('Журналы')
    # Re-resolve the leaf from a fresh tree. Never reuse a cached element index.
    [GingerMenuAccess]::Invoke($window, $processId, 'журналы/текущие прайс-листы')
    $actions.Add('Текущие прайс-листы')
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not [GingerMenuAccess]::PricesVisible($window, $processId)) {
        if ($deadline.Elapsed.TotalSeconds -ge 10) { throw 'Price table not verified after navigation; do not retry automatically' }
        Start-Sleep -Milliseconds 200
    }
}
$menu = @([GingerMenuAccess]::Menu($window, $processId) | Where-Object { $_.Path -in @('журналы', 'журналы/текущие прайс-листы') } | ForEach-Object { @{name=$_.Name; path=$_.Path; state=$_.State} })
[pscustomobject]@{
    source = 'Ginger semantic navigation'; mode = $Mode; process_id = $processId
    prices_visible = [GingerMenuAccess]::PricesVisible($window, $processId)
    actions = $actions.ToArray(); menu = $menu
    headers = [GingerMenuAccess]::Headers($window, $processId).ToArray()
    catalogue_changed = $false; row_identity_verified = $false
} | ConvertTo-Json -Depth 6
