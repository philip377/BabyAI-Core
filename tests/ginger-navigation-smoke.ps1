# Run with Windows PowerShell 5.1 -STA. All actions target this test's own form.
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/windows/ginger-navigation.ps1'))
$boundary = $source.IndexOf('$processes = @(')
if ($boundary -lt 0) { throw 'Missing driver boundary' }
Invoke-Expression $source.Substring(0, $boundary)
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object Windows.Forms.Form
$menu = New-Object Windows.Forms.MenuStrip
$menu.Name = 'menuStrip1'
$menu.AccessibleName = 'menuStrip1'
$journalName = -join ([char[]](0x416,0x443,0x440,0x43d,0x430,0x43b,0x44b))
$pricesName = -join ([char[]](0x422,0x435,0x43a,0x443,0x449,0x438,0x435,0x20,0x43f,0x440,0x430,0x439,0x441,0x2d,0x43b,0x438,0x441,0x442,0x44b))
$journal = New-Object Windows.Forms.ToolStripMenuItem($journalName)
$prices = New-Object Windows.Forms.ToolStripMenuItem($pricesName)
$script:invocations = 0
$prices.Add_Click({ $script:invocations++ })
[void]$journal.DropDownItems.Add($prices)
[void]$menu.Items.Add($journal)
$form.Controls.Add($menu)
$form.MainMenuStrip = $menu
try {
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $root = $form.Handle
    $ownerId = [uint32]$PID
    $path = [GingerMenuAccess]::Normalize($journalName)
    $leaf = $path + '/' + [GingerMenuAccess]::Normalize($pricesName)
    $nodes = [GingerMenuAccess]::Menu($root, $ownerId)
    if (@($nodes | Where-Object Path -eq $leaf).Count -ne 1) { throw 'Leaf not resolved' }
    foreach ($bad in @('delete', ($leaf + '/delete'))) {
        $denied = $false
        try { [GingerMenuAccess]::Invoke($root, $ownerId, $bad) } catch { $denied = $true }
        if (-not $denied) { throw 'Unknown action allowed' }
    }
    $denied = $false
    try { [GingerMenuAccess]::Invoke($root, ($ownerId + 1), $path) } catch { $denied = $true }
    if (-not $denied) { throw 'Wrong process allowed' }
    [GingerMenuAccess]::Invoke($root, $ownerId, $path)
    [Windows.Forms.Application]::DoEvents()
    $prices.Enabled = $false
    $denied = $false
    try { [GingerMenuAccess]::Invoke($root, $ownerId, $leaf) } catch { $denied = $true }
    if (-not $denied -or $script:invocations -ne 0) { throw 'Disabled leaf invoked' }
    $prices.Enabled = $true
    [GingerMenuAccess]::Invoke($root, $ownerId, $leaf)
    if ($script:invocations -ne 1) { throw 'Expected exactly one leaf invocation' }
    if ([GingerMenuAccess]::PricesVisible($root, $ownerId)) { throw 'False table recognition' }
    Write-Output 'PASS: semantic path, whitelist, process ownership, disabled control, one invocation, absent table'
} finally { $form.Dispose() }
