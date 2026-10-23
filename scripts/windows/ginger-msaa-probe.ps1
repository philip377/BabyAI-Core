# Read-only Ginger4 ListView row metadata through Windows MSAA (oleacc).
# This does not click, select, type, press OK, change Ginger, or access SQL.
# Optional -IncludeNames reveals product/word labels from the visible tables.
[CmdletBinding()]
param(
    [ValidateRange(1, 50)][int]$MaxSampleRows = 12,
    [ValidateRange(10, 48)][int]$MaxDepth = 36,
    [ValidateRange(50, 3000)][int]$MaxVisited = 600,
    [switch]$IncludeNames
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") {
    throw "This read-only Ginger MSAA probe must run on Windows."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName Accessibility

# The MSAA client accessibility interface reads public accessible children of
# native ListView controls. No remote memory allocation or pointer injection.
Add-Type -ReferencedAssemblies @([Accessibility.IAccessible].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Accessibility;

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
    public GingerMsaaResult()
    {
        rows = new List<GingerMsaaRow>();
    }
}

public static class GingerMsaaReader
{
    [DllImport("oleacc.dll", PreserveSig = true, ExactSpelling = true)]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

    public static GingerMsaaResult Read(IntPtr hwnd, int maxRows, bool includeNames)
    {
        GingerMsaaResult result = new GingerMsaaResult();
        result.status = "not_available";
        if (hwnd == IntPtr.Zero || maxRows < 1)
            return result;

        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IAccessible accessible = null;
        try
        {
            // OBJID_CLIENT (-4), expressed as unsigned DWORD.
            int hr = AccessibleObjectFromWindow(
                hwnd, unchecked((uint)-4), ref iid, out accessible);
            if (hr < 0 || accessible == null)
            {
                result.status = "msaa_not_exposed";
                return result;
            }
            int count = accessible.accChildCount;
            result.accessible_child_count = count;
            result.status = "ok";

            int take = Math.Min(Math.Max(0, count), maxRows);
            for (int id = 1; id <= take; id++)
            {
                GingerMsaaRow row = new GingerMsaaRow();
                row.child_id = id;
                row.status = "ok";
                try
                {
                    object role = accessible.get_accRole(id);
                    row.role = role == null ? "" : Convert.ToString(role);
                }
                catch (COMException) { row.status = "role_unavailable"; }
                if (includeNames)
                {
                    try
                    {
                        string name = accessible.get_accName(id);
                        row.name = name == null ? "" :
                            name.Substring(0, Math.Min(name.Length, 240));
                    }
                    catch (COMException) { row.status = "name_unavailable"; }
                }
                result.rows.Add(row);
            }
        }
        catch (COMException) { result.status = "msaa_com_error"; }
        catch (InvalidCastException) { result.status = "msaa_interface_unavailable"; }
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
    throw "Exactly one visible Ginger4 main window is required."
}
$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    $processes[0].MainWindowHandle)
if ($null -eq $root) {
    throw "Cannot inspect the Ginger4 window."
}

$script:walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$script:visited = 0
$script:tables = [System.Collections.Generic.List[object]]::new()

function Read-GingerListViews {
    param(
        [System.Windows.Automation.AutomationElement]$Node,
        [int]$Depth
    )
    if ($Depth -gt $MaxDepth -or $script:visited -ge $MaxVisited) { return }
    $script:visited++
    try {
        $current = $Node.Current
        $className = [string]$current.ClassName
        if ($className -match '(^|[.])SysListView32([.]|$)') {
            $bounds = $current.BoundingRectangle
            $handleNumber = [int64]$current.NativeWindowHandle
            $msaa = if ($handleNumber -gt 0) {
                [GingerMsaaReader]::Read(
                    [IntPtr]::new($handleNumber), $MaxSampleRows, [bool]$IncludeNames)
            } else {
                [GingerMsaaResult]::new()
            }
            $script:tables.Add([pscustomobject]@{
                automation_id = [string]$current.AutomationId
                x = [math]::Round($bounds.X)
                y = [math]::Round($bounds.Y)
                width = [math]::Round($bounds.Width)
                height = [math]::Round($bounds.Height)
                msaa_status = $msaa.status
                accessible_child_count = $msaa.accessible_child_count
                sample = $msaa.rows.ToArray()
            })
        }
    } catch {
        return
    }
    try { $child = $script:walker.GetFirstChild($Node) } catch { return }
    while ($null -ne $child -and $script:visited -lt $MaxVisited) {
        Read-GingerListViews -Node $child -Depth ($Depth + 1)
        try { $child = $script:walker.GetNextSibling($child) } catch { break }
    }
}

Read-GingerListViews -Node $root -Depth 0

[pscustomobject]@{
    source = "Ginger4 MSAA native ListView read-only inspection"
    process_name = "Ginger4"
    visited_controls = $script:visited
    max_depth = $MaxDepth
    truncated = ($script:visited -ge $MaxVisited)
    names_included = [bool]$IncludeNames
    listview_count = $script:tables.Count
    listviews = $script:tables.ToArray()
    warning = "NO input or database writes. If IncludeNames is true, review text before sharing."
} | ConvertTo-Json -Depth 8
