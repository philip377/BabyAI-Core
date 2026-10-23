# Read-only diagnostic for the two Ginger4 bottom matching tables.
# Does NOT click, focus, select, expand, edit, read supplier names, or touch SQL.
# Reports only UIA control metadata, supported patterns, and native row counts.
[CmdletBinding()]
param(
    [ValidateRange(15, 48)][int]$MaxDepth = 32,
    [ValidateRange(100, 5000)][int]$MaxVisited = 1000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    throw "Ginger list-view inspection runs only on Windows."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# LVM_GETITEMCOUNT contains no cross-process pointers and does not change
# the selection or content. Timeout prevents hanging on stalled UI threads.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class GingerListViewNativeProbe
{
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
'@

$processes = @(Get-Process -Name Ginger4, Ginger -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
if ($processes.Count -ne 1) {
    throw "Exactly one visible Ginger4 main window is required."
}
$process = $processes[0]
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if ($null -eq $root) {
    throw "Cannot inspect Ginger4 window with UI Automation."
}

$script:walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$script:visited = 0
$script:listviews = [System.Collections.Generic.List[object]]::new()

function Read-MatchingListView {
    param(
        [System.Windows.Automation.AutomationElement]$Node,
        [int]$Depth
    )
    if ($Depth -gt $MaxDepth -or $script:visited -ge $MaxVisited) { return }
    $script:visited++
    try {
        $state = $Node.Current
        $className = [string]$state.ClassName
        if ($className -match '(^|[.])SysListView32([.]|$)') {
            $bounds = $state.BoundingRectangle
            $handleNumber = [int64]$state.NativeWindowHandle

            $patterns = [System.Collections.Generic.List[string]]::new()
            $patternState = "ok"
            try {
                foreach ($pattern in $Node.GetSupportedPatterns()) {
                    $patterns.Add([string]$pattern.ProgrammaticName)
                }
            } catch {
                $patternState = "unavailable"
            }

            $nativeCount = $null
            $nativeStatus = "unavailable"
            if ($handleNumber -gt 0) {
                $nativeResult = [IntPtr]::Zero
                try {
                    $sent = [GingerListViewNativeProbe]::SendMessageTimeout(
                        [IntPtr]::new($handleNumber),
                        [uint32]0x1004, # LVM_GETITEMCOUNT
                        [IntPtr]::Zero,
                        [IntPtr]::Zero,
                        [uint32]2, # SMTO_ABORTIFHUNG
                        [uint32]250,
                        [ref]$nativeResult
                    )
                    if ($sent -ne [IntPtr]::Zero) {
                        $nativeStatus = "ok"
                        $nativeCount = $nativeResult.ToInt64()
                    } else {
                        $nativeStatus = "timed_out_or_denied"
                    }
                } catch {
                    $nativeStatus = "exception"
                }
            }

            $script:listviews.Add([pscustomobject]@{
                depth = $Depth
                class_name = $className
                automation_id = [string]$state.AutomationId
                native_handle_present = ($handleNumber -gt 0)
                enabled = [bool]$state.IsEnabled
                x = [math]::Round($bounds.X)
                y = [math]::Round($bounds.Y)
                width = [math]::Round($bounds.Width)
                height = [math]::Round($bounds.Height)
                uia_patterns = $patterns.ToArray()
                uia_pattern_state = $patternState
                native_item_count = $nativeCount
                native_item_count_status = $nativeStatus
            })
        }
    } catch {
        # Dynamic Windows Forms controls can disappear between traversals.
        return
    }

    try { $child = $script:walker.GetFirstChild($Node) } catch { return }
    while ($null -ne $child -and $script:visited -lt $MaxVisited) {
        Read-MatchingListView -Node $child -Depth ($Depth + 1)
        try { $child = $script:walker.GetNextSibling($child) } catch { break }
    }
}

Read-MatchingListView -Node $root -Depth 0

[pscustomobject]@{
    source = "Ginger4 native list-view read-only inspection"
    process_name = $process.ProcessName
    visited_controls = $script:visited
    max_depth = $MaxDepth
    truncated_by_visited_limit = ($script:visited -ge $MaxVisited)
    listview_count = $script:listviews.Count
    listviews = $script:listviews.ToArray()
    warning = "No product names, row text, mouse/keyboard input, SQL, or data changes."
} | ConvertTo-Json -Depth 8
