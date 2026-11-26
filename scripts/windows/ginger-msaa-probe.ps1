# Read-only Ginger4 native HWND + MSAA ListView diagnostics.
# Discovers descendant Win32 windows directly: does not depend on UIA depth.
# No click, focus, selection, text entry, process-memory access, or SQL writes.
# With -IncludeNames, up to MaxSampleRows accessibility labels may be exposed.
[CmdletBinding()]
param(
    [ValidateRange(1, 50)][int]$MaxSampleRows = 12,
    [ValidateRange(50, 3000)][int]$MaxVisited = 600,
    [switch]$IncludeNames
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") {
    throw "Ginger4 native read-only inspection must run on Windows."
}

Add-Type -AssemblyName Accessibility

Add-Type -ReferencedAssemblies @([Accessibility.IAccessible].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

public sealed class GingerNativeChild
{
    public long handle { get; set; }
    public string class_name { get; set; }
    public int x { get; set; }
    public int y { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public bool visible { get; set; }
}

public sealed class GingerMsaaRow
{
    public int child_id { get; set; }
    public string role { get; set; }
    public string status { get; set; }
    public string name { get; set; }
}

public sealed class GingerMsaaResult
{
    public string status { get; set; }
    public int? accessible_child_count { get; set; }
    public List<GingerMsaaRow> rows { get; set; }
    public GingerMsaaResult() { rows = new List<GingerMsaaRow>(); }
}

public static class GingerNativeInspector
{
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(
        IntPtr root, EnumChildProc callback, IntPtr lparam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(
        IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wparam, IntPtr lparam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);

    [DllImport("oleacc.dll", PreserveSig = true, ExactSpelling = true)]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    public static List<GingerNativeChild> Enumerate(IntPtr root, int maxVisited)
    {
        List<GingerNativeChild> result = new List<GingerNativeChild>();
        EnumChildProc callback = delegate(IntPtr hwnd, IntPtr unused)
        {
            if (result.Count >= maxVisited) return false;
            StringBuilder name = new StringBuilder(512);
            int characters = GetClassNameW(hwnd, name, name.Capacity);
            RECT rect;
            bool positioned = GetWindowRect(hwnd, out rect);
            result.Add(new GingerNativeChild {
                handle = hwnd.ToInt64(),
                class_name = characters > 0 ? name.ToString() : "",
                x = positioned ? rect.left : 0,
                y = positioned ? rect.top : 0,
                width = positioned ? Math.Max(0, rect.right - rect.left) : 0,
                height = positioned ? Math.Max(0, rect.bottom - rect.top) : 0,
                visible = IsWindowVisible(hwnd)
            });
            return result.Count < maxVisited;
        };
        EnumChildWindows(root, callback, IntPtr.Zero);
        return result;
    }

    public static long? GetListItemCount(long hwnd)
    {
        if (hwnd == 0) return null;
        IntPtr count;
        IntPtr success = SendMessageTimeout(
            new IntPtr(hwnd), 0x1004, IntPtr.Zero, IntPtr.Zero,
            0x0002, 250, out count);
        return success != IntPtr.Zero ? (long?)count.ToInt64() : null;
    }

    public static GingerMsaaResult ReadMsaa(long handle, int sampleCount, bool includeNames)
    {
        GingerMsaaResult result = new GingerMsaaResult();
        result.status = "unavailable";
        if (handle == 0) return result;
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IAccessible accessible = null;
        try
        {
            int hr = AccessibleObjectFromWindow(
                new IntPtr(handle), unchecked((uint)-4), ref iid, out accessible);
            if (hr < 0 || accessible == null)
            {
                result.status = "msaa_not_exposed";
                return result;
            }
            int count = accessible.accChildCount;
            result.accessible_child_count = count;
            result.status = "ok";
            for (int id = 1; id <= Math.Min(count, sampleCount); id++)
            {
                GingerMsaaRow row = new GingerMsaaRow {
                    child_id = id, status = "ok", role = "", name = ""
                };
                try {
                    object role = accessible.get_accRole(id);
                    row.role = role == null ? "" : Convert.ToString(role);
                } catch (COMException) {
                    row.status = "role_unavailable";
                }
                if (includeNames)
                {
                    try {
                        string name = accessible.get_accName(id);
                        row.name = name == null ? "" :
                            name.Substring(0, Math.Min(name.Length, 240));
                    } catch (COMException) {
                        row.status = "name_unavailable";
                    }
                }
                result.rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            result.status = "msaa_error_" + ex.GetType().Name;
        }
        finally
        {
            if (accessible != null && Marshal.IsComObject(accessible))
                Marshal.ReleaseComObject(accessible);
        }
        return result;
    }
}
'@

$processes = @(Get-Process -Name Ginger4, Ginger -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
if ($processes.Count -ne 1) {
    throw "Exactly one visible Ginger4 window is required. Open supplier price list."
}

$windows = [GingerNativeInspector]::Enumerate(
    $processes[0].MainWindowHandle, $MaxVisited)
$all = @($windows.ToArray())
$listviews = [System.Collections.Generic.List[object]]::new()
foreach ($window in $all) {
    if ($window.class_name -notmatch 'SysListView32') { continue }
    $msaa = [GingerNativeInspector]::ReadMsaa(
        $window.handle, $MaxSampleRows, [bool]$IncludeNames)
    $listviews.Add([pscustomobject]@{
        class_name = $window.class_name
        x = $window.x
        y = $window.y
        width = $window.width
        height = $window.height
        visible = $window.visible
        native_item_count = [GingerNativeInspector]::GetListItemCount($window.handle)
        msaa_status = $msaa.status
        accessible_child_count = $msaa.accessible_child_count
        sample = $msaa.rows.ToArray()
    })
}
$topClasses = @(
    $all |
    Group-Object -Property class_name |
    Sort-Object -Property Count -Descending |
    Select-Object -First 15 |
    ForEach-Object {
        [pscustomobject]@{ class_name = $_.Name; count = $_.Count }
    }
)

[pscustomobject]@{
    source = "Ginger4 Win32 HWND and MSAA read-only inspection"
    process_name = $processes[0].ProcessName
    native_child_windows_seen = $windows.Count
    max_visited = $MaxVisited
    truncated = ($windows.Count -ge $MaxVisited)
    names_included = [bool]$IncludeNames
    listview_count = $listviews.Count
    listviews = $listviews.ToArray()
    top_window_classes = $topClasses
    warning = "No input or database writes. Review optional product labels before sharing."
} | ConvertTo-Json -Depth 9
