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
    $grid = New-Object Windows.Forms.DataGridView
    $grid.Dock = 'Fill'
    foreach ($column in @('Номер', 'Поставщик', 'Дата', 'Вкл в сводный')) {
        [void]$grid.Columns.Add($column, $column)
    }
    $form.Controls.Add($grid)
    $grid.BringToFront()
    [Windows.Forms.Application]::DoEvents()
    if (-not [GingerMenuAccess]::PricesVisible($root, $ownerId)) { throw 'DataGridView headers not recognized' }
    $grid.Columns[3].HeaderText = 'Unrelated'
    if ([GingerMenuAccess]::PricesVisible($root, $ownerId)) { throw 'Wrong checkbox column accepted' }
    $grid.Columns[3].HeaderText = 'Вкл в сводный'
    $grid.ColumnHeadersVisible = $false
    if ([GingerMenuAccess]::PricesVisible($root, $ownerId)) { throw 'Hidden headers accepted' }
    $grid.ColumnHeadersVisible = $true
    $other = New-Object Windows.Forms.DataGridView
    foreach ($column in @('Номер', 'Поставщик', 'Дата', 'Вкл в сводный')) {
        [void]$other.Columns.Add($column, $column)
    }
    $form.Controls.Add($other)
    $other.BringToFront()
    [Windows.Forms.Application]::DoEvents()
    if ([GingerMenuAccess]::PricesVisible($root, $ownerId)) { throw 'Ambiguous tables accepted' }
    $other.Dispose()
    $grid.AllowUserToAddRows = $false
    $grid.Columns.RemoveAt(3)
    $check = New-Object Windows.Forms.DataGridViewCheckBoxColumn
    $check.HeaderText = 'Вкл в сводный'
    [void]$grid.Columns.Add($check)
    [void]$grid.Rows.Add([object[]]@('1','Supplier A','2026-10-30',$true))
    [void]$grid.Rows.Add([object[]]@('2','Supplier B','2026-10-30',$false))
    [void]$grid.Rows.Add([object[]]@('3','Supplier A','2026-10-30',$true))
    [Windows.Forms.Application]::DoEvents()
    $snapshot = [GingerMenuAccess]::ReadPrices($root, $ownerId)
    if ($snapshot.Rows.Length -ne 3 -or $snapshot.Queue.Length -ne 2 -or
        $snapshot.Queue[0].Number -ne '1' -or $snapshot.Queue[1].Number -ne '3' -or
        $snapshot.CanWrite -or $snapshot.IdentityVerified) { throw 'Incorrect read-only queue' }
    $grid.Rows[2].Cells[0].Value = '1'
    $denied = $false
    try { [GingerMenuAccess]::ReadPrices($root, $ownerId) | Out-Null } catch { $denied = $true }
    if (-not $denied) { throw 'Duplicate price number accepted' }
    $grid.Rows[2].Cells[0].Value = '3'
    $grid.Rows[2].Cells[1].Value = ''
    $denied = $false
    try { [GingerMenuAccess]::ReadPrices($root, $ownerId) | Out-Null } catch { $denied = $true }
    if (-not $denied) { throw 'Missing supplier accepted' }
    foreach ($case in @(@('True',0), @('False',16), @('True',48), @('unknown',0))) {
        $denied = $false
        try { [GingerMenuAccess]::CheckedValue($case[0], $case[1]) | Out-Null } catch { $denied = $true }
        if (-not $denied) { throw 'Ambiguous checkbox accepted' }
    }
    Write-Output 'PASS: menu guards; DataGridView headers; wrong, hidden, absent and ambiguous tables rejected'
    Write-Output 'PASS: ordered checked-price queue; duplicate numbers and inconsistent checkboxes rejected'
} finally { $form.Dispose() }
