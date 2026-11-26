# Read-only Windows UI Automation inspection for the owner-operated Ginger4 client.
# Does not click, press keys, invoke controls, access SQL, or modify the window.
# By default, hides element names (which might contain customer/supplier data).
# Run in a local Windows PowerShell session with Ginger4 already open.
[CmdletBinding()]
param(
    [ValidateRange(1, 32)][int]$MaxDepth = 24,
    [ValidateRange(1, 2500)][int]$MaxElements = 1200,
    [ValidateSet("Both", "Control", "Raw")][string]$View = "Both",
    [switch]$IncludeNames
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    throw "This read-only probe must run on Windows with Ginger4 open."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$processes = @(Get-Process -Name Ginger4, Ginger -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
if ($processes.Count -eq 0) {
    throw "Could not locate a visible Ginger4 process. Open the supplier price list first."
}
if ($processes.Count -gt 1) {
    throw "Multiple Ginger4 windows detected. Close other instances before inspection."
}
$gingerProcess = $processes[0]
$root = [System.Windows.Automation.AutomationElement]::FromHandle($gingerProcess.MainWindowHandle)
if ($null -eq $root) {
    throw "Windows UI Automation cannot inspect the selected Ginger4 main window."
}

$script:found = 0
$script:rows = [System.Collections.Generic.List[object]]::new()
$script:walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Read-GingerElement {
    param(
        [System.Windows.Automation.AutomationElement]$Node,
        [int]$Depth
    )
    if ($script:found -ge $MaxElements -or $Depth -gt $MaxDepth) { return }

    try {
        $state = $Node.Current
        $bounds = $state.BoundingRectangle
        $row = [ordered]@{
            depth = $Depth
            control_type = [string]$state.ControlType.ProgrammaticName
            automation_id = [string]$state.AutomationId
            class_name = [string]$state.ClassName
            enabled = [bool]$state.IsEnabled
            offscreen = [bool]$state.IsOffscreen
            x = [math]::Round($bounds.X)
            y = [math]::Round($bounds.Y)
            width = [math]::Round($bounds.Width)
            height = [math]::Round($bounds.Height)
        }
        if ($IncludeNames -and -not $state.IsPassword) {
            $row["name"] = [string]$state.Name
        }
        $script:rows.Add([pscustomobject]$row)
        $script:found++
    }
    catch {
        # UI Automation elements can disappear while the window updates.
        return
    }

    if ($Depth -ge $MaxDepth -or $script:found -ge $MaxElements) { return }
    try { $child = $script:walker.GetFirstChild($Node) } catch { return }
    while ($null -ne $child -and $script:found -lt $MaxElements) {
        Read-GingerElement -Node $child -Depth ($Depth + 1)
        try { $child = $script:walker.GetNextSibling($child) } catch { break }
    }
}

# Scan both control and raw UIA trees. Many custom WinForms components
# expose container-only nodes in ControlView but deeper children in RawView.
# Independent caps prevent runaway enumeration on a busy business app.
$scans = [System.Collections.Generic.List[object]]::new()
$viewNames = if ($View -eq "Both") { @("Control", "Raw") } else { @($View) }

foreach ($viewName in $viewNames) {
    $script:found = 0
    $script:rows = [System.Collections.Generic.List[object]]::new()
    $script:walker = if ($viewName -eq "Raw") {
        [System.Windows.Automation.TreeWalker]::RawViewWalker
    } else {
        [System.Windows.Automation.TreeWalker]::ControlViewWalker
    }
    Read-GingerElement -Node $root -Depth 0
    $depthReached = if ($script:rows.Count -gt 0) {
        ($script:rows | Measure-Object -Property depth -Maximum).Maximum
    } else { 0 }
    $scans.Add([pscustomobject]@{
        view = $viewName
        elements_seen = $script:found
        deepest_recorded = [int]$depthReached
        max_depth_hit = ($depthReached -ge $MaxDepth)
        max_elements_hit = ($script:found -ge $MaxElements)
        elements = $script:rows.ToArray()
    })
}

[pscustomobject]@{
    source = "Ginger UI Automation read-only inspection"
    process_name = $gingerProcess.ProcessName
    max_depth = $MaxDepth
    max_elements_per_view = $MaxElements
    names_included = [bool]$IncludeNames
    warning = "No input, clicks, SQL calls, or product linking performed."
    views = $scans.ToArray()
} | ConvertTo-Json -Depth 8
