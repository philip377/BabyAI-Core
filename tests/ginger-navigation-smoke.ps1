# Run with Windows PowerShell 5.1 -STA. All actions target this test's own form.
param([switch]$InteractiveInput)
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/windows/ginger-navigation.ps1'))
# The fixture must keep pumping its own window while the driver runs on another thread.
$source = $source.Replace('public static class GingerMenuAccess {', 'public static class GingerMenuAccess { public static System.Threading.Tasks.Task ActivateFixtureAsync(IntPtr root,uint owner) { return System.Threading.Tasks.Task.Run(() => ActivateForRead(root,owner)); }')
$boundary = $source.IndexOf('$processes = @(')
if ($boundary -lt 0) { throw 'Missing driver boundary' }
Invoke-Expression $source.Substring(0, $boundary)
if(-not [GingerMenuAccess]::IsActiveSelectionRgb(0,120,212)){throw 'Active blue rejected'}
foreach($color in @(@(173,216,230),@(255,255,225),@(255,255,255),@(0,0,0))){
 if([GingerMenuAccess]::IsActiveSelectionRgb($color[0],$color[1],$color[2])){throw 'Inactive color accepted'}
}
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
$extra = New-Object Windows.Forms.ToolStripMenuItem('Дополнительно')
$matching = New-Object Windows.Forms.ToolStripMenuItem('Сопоставление снизу')
$script:matchingInvocations = 0
$script:changeWhileOpening = $false
$matching.Add_Click({ $script:matchingInvocations++ })
$extra.Add_DropDownOpening({ if ($script:changeWhileOpening) { $grid.Rows[0].Cells[1].Value = 'Changed during menu' } })
[void]$extra.DropDownItems.Add($matching)
[void]$menu.Items.Add($extra)
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
    $next=[GingerMenuAccess]::NextIncluded($snapshot,'1')
    if($next.Number -ne '3'){throw 'Queue skipped order or confused identical suppliers'}
    if($null -ne [GingerMenuAccess]::NextIncluded($snapshot,'3')){throw 'Queue wraps at end'}
    foreach($after in @('', 'missing', '2')) {
        $denied=$false
        try{[void][GingerMenuAccess]::NextIncluded($snapshot,$after)}catch{$denied=$true}
        if(-not $denied){throw 'Invalid queue anchor accepted'}
    }
    $snapshot.Rows[2].Number='1'
    $denied=$false
    try{[void][GingerMenuAccess]::NextIncluded($snapshot,'1')}catch{$denied=$true}
    if(-not $denied){throw 'Duplicate queue number accepted'}
    $snapshot.Rows[2].Number='3'
    Write-Output 'PASS: next included price preserves order; missing, unchecked and duplicate anchors rejected; no wrap'
    $grid.SelectionMode = 'FullRowSelect'
    $snapshot = [GingerMenuAccess]::ReadPrices($root, $ownerId)
    foreach ($request in @(@('3',$false), @('2',$true))) {
        $denied = $false
        try { [GingerMenuAccess]::SelectPrice($root,$ownerId,$snapshot,$request[0],$request[1]) | Out-Null } catch { $denied = $true }
        if (-not $denied) { throw 'Unconfirmed or unchecked selection accepted' }
    }
    $grid.Rows[2].Cells[1].Value = 'Changed supplier'
    $denied = $false
    try { [GingerMenuAccess]::SelectPrice($root,$ownerId,$snapshot,'3',$true) | Out-Null } catch { $denied = $true }
    if (-not $denied) { throw 'Stale price snapshot accepted' }
    $grid.Rows[2].Cells[1].Value = 'Supplier A'
    $snapshot = [GingerMenuAccess]::ReadPrices($root, $ownerId)
    $selected = [GingerMenuAccess]::SelectPrice($root,$ownerId,$snapshot,'3',$true)
    if (@($selected.Rows | Where-Object Selected).Count -ne 1 -or -not $grid.Rows[2].Selected -or $selected.CanWrite) {
        throw 'Selection result invalid'
    }
    $grid.SelectionMode = 'CellSelect'
    $snapshot = [GingerMenuAccess]::ReadPrices($root, $ownerId)
    $selected = [GingerMenuAccess]::SelectPrice($root,$ownerId,$snapshot,'1',$true)
    if (-not $selected.Rows[0].SupplierFocused -or $grid.CurrentCell.ColumnIndex -ne 1 -or $grid.CurrentCell.RowIndex -ne 0) {
        throw 'Supplier cell focus not verified'
    }
    $grid.ReadOnly = $true
    $script:priceOpens = 0
    $grid.Add_CellDoubleClick({ param($sender,$event) if ($event.ColumnIndex -eq 1 -and $event.RowIndex -eq 0) { $script:priceOpens++ } else { throw 'Wrong double-click target' } })
    $selected = [GingerMenuAccess]::ReadPrices($root,$ownerId)
    $denied = $false
    try { [GingerMenuAccess]::OpenSelectedPrice($root,$ownerId,$selected,'1',$false) } catch { $denied = $true }
    if (-not $denied -or $script:priceOpens -ne 0) { throw 'Unconfirmed price open accepted' }
    if ($InteractiveInput) {
        $activation=[GingerMenuAccess]::ActivateFixtureAsync($root,$ownerId)
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        while(-not $activation.IsCompleted -and $deadline.Elapsed.TotalSeconds -lt 5){
            [Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 10
        }
        if(-not $activation.IsCompleted){throw 'Fixture activation timed out'}
        $activation.GetAwaiter().GetResult()
        [GingerMenuAccess]::OpenSelectedPrice($root,$ownerId,$selected,'1',$true)
        Start-Sleep -Milliseconds 100
        [Windows.Forms.Application]::DoEvents()
        if ($script:priceOpens -ne 1) { throw 'Expected one supplier cell double click' }
        Write-Output 'PASS: interactive supplier-cell double click on fixture only'
    }
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
    $searchPanel = New-Object Windows.Forms.ListView
    $searchPanel.View = 'Details'
    [void]$searchPanel.Columns.Add('Слово')
    [void]$searchPanel.Columns.Add('Поиск')
    $productPanel = New-Object Windows.Forms.ListView
    $productPanel.View = 'Details'
    [void]$productPanel.Columns.Add('Товар')
    [void]$productPanel.Columns.Add('Цена')
    $form.Controls.Add($searchPanel)
    $form.Controls.Add($productPanel)
    $searchPanel.BringToFront()
    $productPanel.BringToFront()
    [Windows.Forms.Application]::DoEvents()
    $view = [GingerMenuAccess]::InspectMatching($root,$ownerId)
    if ($view.Status -ne 'panels_visible_empty' -or $view.CanMatch) { throw 'Empty panels incorrectly classified' }
    [void]$productPanel.Items.Add('Synthetic product')
    $view = [GingerMenuAccess]::InspectMatching($root,$ownerId)
    if ($view.Status -ne 'rows_observed_identity_unverified' -or $view.ProductRows -ne 1 -or $view.CanMatch) { throw 'Row count grants matching permission' }
    $productPanel.Dispose()
    $searchPanel.Dispose()
    $form.Width=700
    $tabHost=New-Object Windows.Forms.Panel
    $tabHost.Width=620; $tabHost.Height=24; $tabHost.Top=50
    $form.Controls.Add($tabHost)
    $tabHost.BringToFront()
    $tabPanels=@()
    for($i=0;$i -lt 3;$i++) {
        $panel=New-Object Windows.Forms.Panel
        $panel.Width=200; $panel.Height=20; $panel.Left=$i*202
        $tabHost.Controls.Add($panel)
        $tabPanels+=$panel
    }
    [Windows.Forms.Application]::DoEvents()
    $tabs=@([GingerMenuAccess]::TabCandidates($root,$ownerId))
    if($tabs.Count -ne 3){throw 'Native three-tab group not found'}
    $denied=$false
    try{[GingerMenuAccess]::ClickVerifiedTab($root,$ownerId,$tabs[1],$false)}catch{$denied=$true}
    if(-not $denied){throw 'Unconfirmed tab click accepted'}
    $tabPanels[1].Left+=10
    $denied=$false
    try{[GingerMenuAccess]::ClickVerifiedTab($root,$ownerId,$tabs[1],$true)}catch{$denied=$true}
    if(-not $denied){throw 'Moved tab target accepted'}
    $tabHost.Dispose()
    $grid.Columns[0].HeaderText='Название в прайсе'
    $grid.Columns[1].HeaderText='Цена'
    $grid.Columns[2].HeaderText='Товар'
    $numberBox=New-Object Windows.Forms.TextBox
    $numberBox.AccessibleName='Номер'; $numberBox.Text='1'
    $supplierBox=New-Object Windows.Forms.TextBox
    $supplierBox.AccessibleName='Поставщик'; $supplierBox.Text='Supplier A'
    $form.Controls.Add($numberBox); $form.Controls.Add($supplierBox)
    [Windows.Forms.Application]::DoEvents()
    $document=[GingerMenuAccess]::ReadPriceDocument($root,$ownerId)
    if($document.Number -ne '1' -or $document.Supplier -ne 'Supplier A' -or $document.CanWrite){throw 'Wrong document identity'}
    $denied=$false
    try{[GingerMenuAccess]::OpenDocumentMatching($root,$ownerId,$document,$false)}catch{$denied=$true}
    if(-not $denied -or $script:matchingInvocations -ne 0){throw 'Unconfirmed document menu accepted'}
    $numberBox.Text='2'
    $denied=$false
    try{[GingerMenuAccess]::OpenDocumentMatching($root,$ownerId,$document,$true)}catch{$denied=$true}
    if(-not $denied -or $script:matchingInvocations -ne 0){throw 'Changed document accepted'}
    $numberBox.Text='1'
    $script:changeDocument=$true
    $extra.Add_DropDownOpening({if($script:changeDocument){$numberBox.Text='2'}})
    $denied=$false
    try{[GingerMenuAccess]::OpenDocumentMatching($root,$ownerId,$document,$true)}catch{$denied=$true}
    if(-not $denied -or $script:matchingInvocations -ne 0){throw 'Document change during menu ignored'}
    $script:changeDocument=$false
    $extra.HideDropDown()
    $numberBox.Text='1'
    [GingerMenuAccess]::OpenDocumentMatching($root,$ownerId,$document,$true)
    if($script:matchingInvocations -ne 1){throw 'Verified document menu not invoked'}
    $extra.HideDropDown()
    $grid.ClearSelection()
    $grid.Rows[0].Cells[0].Value='Synthetic perfume 100 ml'
    $grid.Rows[0].Cells[1].Value='123,45'
    $grid.Rows[0].Cells[2].Value='<не указано>'
    $grid.Rows[0].Cells[0].Selected=$true
    [Windows.Forms.Application]::DoEvents()
    $observed=[GingerMenuAccess]::ReadSelectedProduct($root,$ownerId)
    if($observed.Title -ne 'Synthetic perfume 100 ml' -or $observed.PriceText -ne '123,45' -or
       $observed.LinkedProductText -ne '<не указано>' -or $observed.ObservedRowPosition -ne 1 -or
       $observed.IdentityVerified -or $observed.CanMatch){throw 'Product observation incorrectly identified or authorized'}
    foreach($i in @(1,2)) {
        $grid.Rows[$i].Cells[0].Value='Same displayed product'
        $grid.Rows[$i].Cells[1].Value='125,00'
        $grid.Rows[$i].Cells[2].Value='<none>'
    }
    $rows=[GingerMenuAccess]::ReadProductRows($root,$ownerId)
    $denied=$false
    try{[void][GingerMenuAccess]::SelectNextProduct($root,$ownerId,$rows,$false)}catch{$denied=$true}
    if(-not $denied -or -not $grid.Rows[0].Cells[0].Selected){throw 'Unconfirmed product navigation accepted'}
    $grid.CurrentCell=$grid.Rows[0].Cells[0]
    $wasReadOnly=$grid.ReadOnly
    $grid.ReadOnly=$false
    if(-not $grid.BeginEdit($true)){throw 'Fixture could not enter edit mode'}
    [Windows.Forms.Application]::DoEvents()
    $denied=$false
    try{[void][GingerMenuAccess]::SelectNextProduct($root,$ownerId,$rows,$true)}catch{$denied=$true}
    if(-not $denied -or -not $grid.IsCurrentCellInEditMode){throw 'Product navigation committed active edit'}
    [void]$grid.CancelEdit()
    $grid.ReadOnly=$wasReadOnly
    $saved=$grid.Rows[2].Cells[1].Value
    $grid.Rows[2].Cells[1].Value='Changed unselected row'
    $denied=$false
    try{[void][GingerMenuAccess]::SelectNextProduct($root,$ownerId,$rows,$true)}catch{$denied=$true}
    if(-not $denied -or -not $grid.Rows[0].Cells[0].Selected){throw 'Stale table allowed product navigation'}
    $grid.Rows[2].Cells[1].Value=$saved
    $next=[GingerMenuAccess]::SelectNextProduct($root,$ownerId,$rows,$true)
    if($next.ObservedRowPosition -ne 2 -or $next.CanMatch -or $next.IdentityVerified){throw 'Next row incorrectly selected or authorized'}
    $rows=[GingerMenuAccess]::ReadProductRows($root,$ownerId)
    $next=[GingerMenuAccess]::SelectNextProduct($root,$ownerId,$rows,$true)
    if($next.ObservedRowPosition -ne 3){throw 'Product order lost'}
    $rows=[GingerMenuAccess]::ReadProductRows($root,$ownerId)
    $end=[GingerMenuAccess]::SelectNextProduct($root,$ownerId,$rows,$true)
    if($null -ne $end -or -not $grid.Rows[2].Cells[0].Selected){throw 'Product queue wrapped'}
    $grid.ClearSelection()
    $grid.Rows[0].Cells[0].Selected=$true
    Write-Output 'PASS: next product selection; confirmation, full-table freshness, order, no wrap and no matching permission'
    $candidatesPanel=New-Object Windows.Forms.ListView
    $candidatesPanel.View='Details'
    [void]$candidatesPanel.Columns.Add('Товар')
    [void]$candidatesPanel.Columns.Add('Цена')
    $form.Controls.Add($candidatesPanel)
    $candidatesPanel.BringToFront()
    [Windows.Forms.Application]::DoEvents()
    $empty=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    if($empty.Candidates.Length -ne 0 -or $empty.CanMatch){throw 'Empty candidates grant matching'}
    $retail=$candidatesPanel.Items.Add('Synthetic perfume 100 ml')
    [void]$retail.SubItems.Add('123.45')
    $tester=$candidatesPanel.Items.Add('Synthetic perfume 100 ml (тестер)')
    [void]$tester.SubItems.Add('100.00')
    $snapshot=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    if($snapshot.Candidates.Length -ne 2 -or $snapshot.CanMatch -or $snapshot.IdentityVerified -or
       $snapshot.Candidates[0].IdentityVerified -or $snapshot.Candidates[1].Title -notlike '*(тестер)' -or
       -not $snapshot.Candidates[0].DescriptionText.Contains('123.45')){throw 'Candidate text lost or identity invented'}
    if($snapshot.Candidates[1].Bounds.Length -ne 4 -or $snapshot.Candidates[1].Bounds[2] -le 0){throw 'Candidate bounds missing'}
    $denied=$false
    try{[GingerMenuAccess]::InvokeMatchingCandidate($root,$ownerId,$null,$snapshot,2,$snapshot.Candidates[1].Title,$false)}catch{$denied=$true}
    if(-not $denied){throw 'Unconfirmed candidate invocation accepted'}
    $boundsCheck=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    $boundsCheck.Candidates[1].Bounds[0]+=1
    $denied=$false
    try{[GingerMenuAccess]::RequireSameCandidates($snapshot,$boundsCheck)}catch{$denied=$true}
    if(-not $denied){throw 'Changed candidate geometry accepted'}
    if($InteractiveInput) {
        $form.ClientSize=[Drawing.Size]::new(900,600)
        $candidatesPanel.Location=[Drawing.Point]::new(600,300)
        $candidatesPanel.Size=[Drawing.Size]::new(260,150)
        [Windows.Forms.Application]::DoEvents()
        $focusRows=[GingerMenuAccess]::ReadProductRows($root,$ownerId)
        [void][GingerMenuAccess]::RestoreProductSelection($root,$ownerId,$focusRows,1,$true)
        [Windows.Forms.Application]::DoEvents()
        $script:matchClicks=0
        $candidatesPanel.Add_MouseDoubleClick({
            if($candidatesPanel.SelectedItems.Count -ne 1 -or $candidatesPanel.SelectedItems[0].Text -ne 'Synthetic perfume 100 ml (тестер)'){throw 'Wrong matching double-click target'}
            $script:matchClicks++
        })
        [GingerMenuAccess]::ActivateForRead($root,$ownerId)
        $matchRows=[GingerMenuAccess]::ReadProductRows($root,$ownerId)
        $activeColor=$grid.DefaultCellStyle.SelectionBackColor
        $grid.DefaultCellStyle.SelectionBackColor=[Drawing.Color]::LightBlue
        $grid.Refresh()
        [Windows.Forms.Application]::DoEvents()
        $denied=$false
        try{[GingerMenuAccess]::RequireActiveBlueSource($root,$ownerId,$matchRows,1)}catch{$denied=$true}
        if(-not $denied){throw 'Light blue source allowed matching'}
        $grid.DefaultCellStyle.SelectionBackColor=$activeColor
        $grid.Refresh()
        [Windows.Forms.Application]::DoEvents()
        [GingerMenuAccess]::RequireActiveBlueSource($root,$ownerId,$matchRows,1)
        Write-Output 'PASS: actual light-blue source rejected; active blue accepted'
        $matchCandidates=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
        [GingerMenuAccess]::InvokeMatchingCandidate($root,$ownerId,$matchRows,$matchCandidates,2,'Synthetic perfume 100 ml (тестер)',$true)
        Start-Sleep -Milliseconds 100
        [Windows.Forms.Application]::DoEvents()
        if($script:matchClicks -ne 1){throw 'Expected exactly one candidate double click'}
        Write-Output 'PASS: exactly one physical matching double click on verified fixture candidate'
        $candidatesPanel.SelectedItems.Clear()
        [Windows.Forms.Application]::DoEvents()
        $snapshot=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    }
    $searchPanel=New-Object Windows.Forms.ListView
    $searchPanel.View='Details'
    $searchPanel.CheckBoxes=$true
    [void]$searchPanel.Columns.Add('Слово'); [void]$searchPanel.Columns.Add('Поиск')
    $form.Controls.Add($searchPanel)
    $word=$searchPanel.Items.Add('Synthetic')
    [void]$word.SubItems.Add('Yes')
    [Windows.Forms.Application]::DoEvents()
    $queryOnly=[GingerMenuAccess]::ReadSearchWords($root,$ownerId)
    if($queryOnly.Candidates.Length -ne 1 -or $queryOnly.CanMatch -or $queryOnly.SearchCompletionVerified){throw 'Query-only read lost data or overclaimed'}
    $context=[GingerMenuAccess]::ReadSearchContext($root,$ownerId)
    if($context.SearchWords.Candidates.Length -ne 1 -or $context.SearchWords.Candidates[0].Title -ne 'Synthetic' -or
       $context.Products.Candidates.Length -ne 2 -or $context.CanMatch -or $context.SearchCompletionVerified){throw 'Search context lost or overclaimed'}
    if($context.QueryWordsMatchSource){throw 'Partial query declared full source match'}
    $word.Text='Synthetic perfume 100 ml'
    $lexical=[GingerMenuAccess]::ReadSearchContext($root,$ownerId)
    if(-not $lexical.QueryWordsMatchSource -or $lexical.SearchCompletionVerified -or $lexical.CanMatch){throw 'Lexical query evidence lost or overclaimed'}
    $grid.Rows[0].Cells[0].Value='Synthetic perfume 100ml (with cap)'
    $word.Text='Synthetic perfume 100 ml ( with cap )'
    $tokenized=[GingerMenuAccess]::ReadSearchContext($root,$ownerId)
    if(-not $tokenized.QueryWordsMatchSource -or $tokenized.SearchCompletionVerified -or $tokenized.CanMatch){throw 'Tokenized source mismatch or authority overclaim'}
    $word.Text='Synthetic perfume 10 ml ( with cap )'
    if(([GingerMenuAccess]::ReadSearchContext($root,$ownerId)).QueryWordsMatchSource){throw 'Changed quantity accepted'}
    $grid.Rows[0].Cells[0].Value='Synthetic perfume 100 ml'
    $word.Text='Synthetic'
    if($context.SearchWords.Candidates[0].Checked -ne $false){throw 'Unchecked search word not observed'}
    $word.Checked=$true
    $changedContext=[GingerMenuAccess]::ReadSearchContext($root,$ownerId)
    [GingerMenuAccess]::VerifyEnabledSearchWord($context.SearchWords,$changedContext.SearchWords,'Synthetic')
    $denied=$false
    try{[void][GingerMenuAccess]::EnableSearchWord($root,$ownerId,$context.SearchWords,'Synthetic',$false)}catch{$denied=$true}
    if(-not $denied){throw 'Unconfirmed star action accepted'}
    $denied=$false
    try{[GingerMenuAccess]::VerifyEnabledSearchWord($context.SearchWords,$context.SearchWords,'Synthetic')}catch{$denied=$true}
    if(-not $denied){throw 'Unchanged star accepted as enabled'}
    if(-not [GingerMenuAccess]::SearchWordsDescribeSource(@('CARTIER','DELICES','DE','parfum','30','ml','vintage'),'CARTIER DELICES DE CARTIER parfum 30 ml vintage')){throw 'Nonadjacent duplicate rejected'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('CARTIER','DE','parfum','30','ml','vintage'),'CARTIER DELICES DE CARTIER parfum 30 ml vintage')){throw 'Missing variant accepted'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('A','30','ml','B'),'A 30 ml B 30 ml')){throw 'Nonadjacent quantity removed'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('A','/','B'),'A / B / A')){throw 'Punctuation removed'}
    if(-not [GingerMenuAccess]::SearchWordsDescribeSource(@('HUGO','BOSS','SOUL','90','ml'),'HUGO BOSS BOSS SOUL 90 ml')){throw 'Adjacent duplicate word rejected'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('HUGO','BOSS','90','ml'),'HUGO BOSS BOSS SOUL 90 ml')){throw 'Missing variant accepted after deduplication'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('A','90','ml'),'A 90 90 ml')){throw 'Repeated quantity silently removed'}
    if(-not [GingerMenuAccess]::SearchWordsDescribeSource(@('MARBERT','MAN','TOO','ash','/','l','100','ml'),'MARBERT MAN TOO ash/l 100 ml')){throw 'Punctuation tokenization rejected'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('MARBERT','MANTOO','100','ml'),'MARBERT MAN TOO 100 ml')){throw 'Different words accepted'}
    if([GingerMenuAccess]::SearchWordsDescribeSource(@('MARBERT','100','ml'),'MARBERT 50 ml')){throw 'Different volume accepted'}
    [GingerMenuAccess]::VerifySearchWordState($changedContext.SearchWords,$context.SearchWords,'Synthetic',$false)
    $denied=$false
    try{[GingerMenuAccess]::VerifySearchWordState($changedContext.SearchWords,$changedContext.SearchWords,'Synthetic',$false)}catch{$denied=$true}
    if(-not $denied){throw 'Checked star accepted as disabled'}
    $stableWords=[GingerMenuAccess]::ReadSearchWords($root,$ownerId)
    $stableWords.Candidates[0].Bounds[0]+=1
    $denied=$false
    try{[GingerMenuAccess]::RequireSameCandidates($stableWords,$changedContext.SearchWords)}catch{$denied=$true}
    if(-not $denied){throw 'Moved search row accepted for input'}
    if($changedContext.SearchWords.Candidates[0].Checked -ne $true){throw 'Checked search word not observed'}
    $denied=$false
    try{[GingerMenuAccess]::RequireSameSearchContext($context,$changedContext)}catch{$denied=$true}
    if(-not $denied){throw 'Search checkbox change ignored'}
    $word.Checked=$false
    $word.Text='Changed query'
    $changedContext=[GingerMenuAccess]::ReadSearchContext($root,$ownerId)
    $denied=$false
    try{[GingerMenuAccess]::RequireSameSearchContext($context,$changedContext)}catch{$denied=$true}
    if(-not $denied){throw 'Query change accepted with unchanged candidates'}
    $word.Text='Synthetic'
    $word.SubItems[1].Text='No'
    $changedContext=[GingerMenuAccess]::ReadSearchContext($root,$ownerId)
    $denied=$false
    try{[GingerMenuAccess]::RequireSameSearchContext($context,$changedContext)}catch{$denied=$true}
    if(-not $denied){throw 'Search description change ignored'}
    $searchPanel.Dispose()
    $denied=$false
    try{[void][GingerMenuAccess]::ReadSearchContext($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Missing search words panel accepted'}
    Write-Output 'PASS: search words and candidates; changed query/description and missing panel rejected; no freshness claim'
    $retail.SubItems[1].Text='124.45'
    $changed=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    $denied=$false
    try{[GingerMenuAccess]::RequireSameCandidates($snapshot,$changed)}catch{$denied=$true}
    if(-not $denied){throw 'Changed candidate price accepted'}
    $retail.SubItems[1].Text='123.45'
    $retail.Text='Other product'
    $changed=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    $denied=$false
    try{[GingerMenuAccess]::RequireSameCandidates($snapshot,$changed)}catch{$denied=$true}
    if(-not $denied){throw 'Changed candidate title accepted'}
    $retail.Text='Synthetic perfume 100 ml'
    $grid.Rows[0].Cells[1].Value='999,99'
    $changed=[GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)
    $denied=$false
    try{[GingerMenuAccess]::RequireSameCandidates($snapshot,$changed)}catch{$denied=$true}
    if(-not $denied){throw 'Changed source accepted with old candidates'}
    $grid.Rows[0].Cells[1].Value='123,45'
    $duplicate=New-Object Windows.Forms.ListView
    $duplicate.View='Details'
    [void]$duplicate.Columns.Add('Товар'); [void]$duplicate.Columns.Add('Цена')
    $form.Controls.Add($duplicate)
    [Windows.Forms.Application]::DoEvents()
    $denied=$false
    try{[void][GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Duplicate candidate panel accepted'}
    $duplicate.Dispose()
    for($i=2;$i -lt 257;$i++){[void]$candidatesPanel.Items.Add('Synthetic item '+$i)}
    $denied=$false
    try{[void][GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Candidate limit ignored'}
    $candidatesPanel.Dispose()
    $denied=$false
    try{[void][GingerMenuAccess]::ReadMatchingCandidates($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Missing candidate panel accepted'}
    Write-Output 'PASS: candidate texts and variants read; changed source, price, title, duplicate panels and excess rows rejected'
    # Isolate recovery fixture from the focus established by the physical-click test.
    $grid.CurrentCell=$null
    $grid.ClearSelection()
    $denied=$false
    try{[void][GingerMenuAccess]::ReadSelectedProduct($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Missing product selection accepted'}
    $grid.MultiSelect=$true
    $grid.Rows[0].Cells[0].Selected=$true
    $grid.Rows[1].Cells[0].Selected=$true
    $denied=$false
    try{[void][GingerMenuAccess]::ReadSelectedProduct($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Ambiguous product selection accepted'}
    $evidence=@([GingerMenuAccess]::InspectProductSelection($root,$ownerId))
    $selectedEvidence=@($evidence | Where-Object {@($_.CellStates | Where-Object {($_ -band 2) -ne 0}).Count -gt 0})
    if($selectedEvidence.Count -ne 2 -or $evidence[0].CanWrite){throw 'Multi-selection diagnostic lost evidence or granted write'}
    if(-not $grid.Rows[0].Cells[0].Selected -or -not $grid.Rows[1].Cells[0].Selected){throw 'Diagnostic changed selection'}
    Write-Output 'PASS: ambiguous selection inspected without choosing a source or changing selection'
    $recoveryRows=[GingerMenuAccess]::ReadProductRowsForRecovery($root,$ownerId)
    $denied=$false
    try{[void][GingerMenuAccess]::RestoreProductSelection($root,$ownerId,$recoveryRows,1,$false)}catch{$denied=$true}
    if(-not $denied){throw 'Unconfirmed recovery accepted'}
    $savedTitle=$grid.Rows[2].Cells[0].Value
    $grid.Rows[2].Cells[0].Value='Changed before recovery'
    $denied=$false
    try{[void][GingerMenuAccess]::RestoreProductSelection($root,$ownerId,$recoveryRows,1,$true)}catch{$denied=$true}
    if(-not $denied){throw 'Changed recovery table accepted'}
    $grid.Rows[2].Cells[0].Value=$savedTitle
    $restored=[GingerMenuAccess]::RestoreProductSelection($root,$ownerId,$recoveryRows,1,$true)
    if($restored.ObservedRowPosition -ne 1 -or $restored.CanMatch -or $grid.Rows[1].Cells[0].Selected){throw 'Recovery selection incorrect'}
    Write-Output 'PASS: recovery rejects absent confirmation and changed table; restores one explicit position'

    $grid.ClearSelection()
    $grid.Rows[0].Cells[0].Selected=$true
    $grid.Rows[0].Cells[0].Value=''
    $denied=$false
    try{[void][GingerMenuAccess]::ReadSelectedProduct($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Blank product title accepted'}
    Write-Output 'PASS: selected product read-only observation; absent, multiple and blank selections rejected'
    $numberBox.Dispose();$supplierBox.Dispose()
    Write-Output 'PASS: menu guards; DataGridView headers; wrong, hidden, absent and ambiguous tables rejected'
    Write-Output 'PASS: ordered checked-price queue; duplicate numbers and inconsistent checkboxes rejected'
    Write-Output 'PASS: confirmed selection verified; stale and unchecked requests blocked'
    Write-Output 'PASS: matching view path; confirmation and price change during menu opening enforced'
    $denied=$false
    try{[GingerMenuAccess]::ActivateForRead($root,0)}catch{$denied=$true}
    if(-not $denied){throw 'Wrong owner allowed activation'}
    $denied=$false
    try{[GingerMenuAccess]::RequireForeground([IntPtr]::Zero,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Invalid foreground target accepted'}
    $form.WindowState='Minimized'
    [Windows.Forms.Application]::DoEvents()
    $denied=$false
    try{[GingerMenuAccess]::RequireForeground($root,$ownerId)}catch{$denied=$true}
    if(-not $denied){throw 'Minimized window accepted for input'}
    if($InteractiveInput) {
        $activation=[GingerMenuAccess]::ActivateFixtureAsync($root,$ownerId)
        $activationDeadline=[Diagnostics.Stopwatch]::StartNew()
        while(-not $activation.IsCompleted -and $activationDeadline.Elapsed.TotalSeconds -lt 5) {
            [Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 10
        }
        if(-not $activation.IsCompleted){throw 'Fixture activation timed out'}
        $activation.GetAwaiter().GetResult()
        if($form.WindowState -eq 'Minimized'){throw 'Verified window not restored'}
        Write-Output 'PASS: minimized fixture restored and foreground verified'
    } else { $form.WindowState='Normal' }
    Write-Output 'PASS: native tab geometry guards and supplier document identity checks'
} finally { $form.Dispose() }
