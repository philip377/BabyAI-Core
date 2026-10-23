# Interactive, CPU-only UNIX/Ginger candidate trial for Windows PowerShell 5.1.
# Reads an existing Ginger OCR JSON report. Optionally captures a NEW report
# using the sibling read-only OCR script. NEVER clicks, links, edits SQL,
# reads credentials, uploads data or authorizes ERP actions.
[CmdletBinding()]
param(
    [string]$Report = "",
    [switch]$CaptureFresh,
    [switch]$Demo,
    [switch]$NonInteractive
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-Report {
    if($Demo){
        # Example only. A demo can NEVER be awarded or written to the ledger.
        return [pscustomobject]@{
            source = "Ginger4 CPU OCR pilot"
            read_only = $true
            ginger_modified = $false
            local_only = $true
            selected_row_color_band_detected = $true
            selected_row_visual_candidate = "Bois 1920 Oro Rosso edp 100 mi"
            right_catalogue_candidates = @(
                "Bois 1920 ОВО ROSSO edp 100ml",
                "Bois 1920 ORO ROSSO edp 100ml TESTER",
                "Paolo ... OVO ROSSA edp 100ml"
            )
        }
    }
    $downloads=Join-Path $env:USERPROFILE "Downloads"
    if($CaptureFresh -and $Report.Trim()){
        throw "Choose -CaptureFresh OR -Report, not both."
    }
    if($CaptureFresh){
        $ocr=Join-Path $PSScriptRoot "test-ginger-cpu-ocr.ps1"
        if(-not (Test-Path -LiteralPath $ocr -PathType Leaf)){
            throw "Save test-ginger-cpu-ocr.ps1 alongside this script, or omit -CaptureFresh."
        }
        Write-Host "Reading the current Ginger screen locally..."
        $captureStarted=[DateTime]::UtcNow
        # Capture the child console output locally and echo it via the HOST
        # stream. Do not leak child stdout into Read-Report's return pipeline:
        # that previously produced an array instead of the report object.
        $ocrOutput=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ocr -CaptureSupplierGrid 2>&1)
        $ocrExit=$LASTEXITCODE
        foreach($message in $ocrOutput){Write-Host ([string]$message)}
        if($ocrExit -ne 0){throw "Fresh Ginger OCR failed; no result or score recorded."}
        $createdReports=@()
        foreach($message in $ocrOutput){
            $match=[regex]::Match([string]$message,'^CPU OCR completed:\s*(.+ginger-cpu-report\.json)\s*    if(-not $Report -or -not (Test-Path -LiteralPath $Report -PathType Leaf)){
        throw "No Ginger report found. Use -CaptureFresh or -Report path to ginger-cpu-report.json."
    }
    $file=Get-Item -LiteralPath $Report
    if($file.Length -gt 4194304){throw "OCR report too large"}
    $document=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if($null -eq $document -or $document.source -ne "Ginger4 CPU OCR pilot" -or
       $document.read_only -cne $true -or $document.ginger_modified -cne $false -or
       $document.local_only -cne $true){
        throw "Report does not have valid Ginger read-only and local-only confirmation."
    }
    $required=@("selected_row_visual_candidate","selected_row_color_band_detected","right_catalogue_candidates")
    foreach($field in $required){
        if($null -eq $document.PSObject.Properties[$field]){
            throw "OCR report lacks '$field'. Download the current OCR script and rerun -CaptureFresh."
        }
    }
    if($document.selected_row_color_band_detected -cne $true -or
       [string]::IsNullOrWhiteSpace([string]$document.selected_row_visual_candidate)){
        throw "The selected supplier title is unreadable. Select one Ginger row and rerun -CaptureFresh."
    }
    if($null -eq $document.right_catalogue_candidates -or
       $document.right_catalogue_candidates -isnot [array]){
        throw "OCR report has an invalid right-catalogue list; stop without scoring."
    }
    return $document
}

function Get-Volume([string]$title){
    $m=[regex]::Match($title,'(?i)(?<!\d)(\d{1,4})\s*(ml|mi|m!|m1|мл)(?![a-zа-я0-9])')
    if(-not $m.Success){return [pscustomobject]@{size=0;uncertain=$true}}
    return [pscustomobject]@{
        size=[int]$m.Groups[1].Value
        uncertain=($m.Groups[2].Value.ToLowerInvariant() -notin @("ml","мл"))
    }
}

function Get-Variant([string]$title){
    $m=[regex]::Match($title,'(?i)\b(intense|qahwa|elixir|extreme|absolu)\b')
    if($m.Success){return $m.Value.ToLowerInvariant()}
    return ""
}

function Get-Concentration([string]$title){
    $m=[regex]::Match($title,'(?i)\b(edp|edt|edc|extrait)\b')
    if($m.Success){return $m.Value.ToLowerInvariant()}
    return ""
}

function Get-Tester([string]$title){
    return [bool]([regex]::IsMatch($title,'(?i)(tester|testeur|тестер|тест)(?![а-яa-z])'))
}

function Test-Candidate([string]$source,[string]$candidate){
    $title=([regex]::Replace($candidate,'\s+\d{1,6}[.,]\d{2}\s*$','')).Trim()
    $firstSource=([regex]::Match($source,'^[^\s]+')).Value.ToLowerInvariant()
    $firstCandidate=([regex]::Match($title,'^[^\s]+')).Value.ToLowerInvariant()
    $srcVolume=Get-Volume $source
    $dstVolume=Get-Volume $title
    $srcConc=Get-Concentration $source
    $dstConc=Get-Concentration $title
    $srcVariant=Get-Variant $source
    $dstVariant=Get-Variant $title
    $classification="review"
    $reason="OCR is not proof of catalogue identity: compare the actual perfume, volume and packaging."
    $decision="abstain"
    if($firstSource -ne $firstCandidate){
        $classification="different_brand"
        $reason="Different first brand word."
        $decision="reject"
    }elseif((Get-Tester $source) -ne (Get-Tester $title)){
        $classification="tester_mismatch"
        $reason="Tester and regular bottle are different items."
        $decision="reject"
    }elseif($srcVariant -ne $dstVariant){
        $classification="variant_mismatch"
        $reason="Different fragrance variant."
        $decision="reject"
    }elseif($srcConc -and $dstConc -and $srcConc -ne $dstConc){
        $classification="concentration_mismatch"
        $reason="EDP/EDT concentration differs."
        $decision="reject"
    }elseif($srcVolume.size -ne 0 -and $dstVolume.size -ne 0 -and
            $srcVolume.size -ne $dstVolume.size){
        $classification="volume_mismatch"
        $reason="Bottle size differs."
        $decision="reject"
    }
    # Never mark any OCR title as an automatically linkable exact match.
    return [pscustomobject]@{
        candidate=$title
        classification=$classification
        decision=$decision
        reason=$reason
        uncertain_ocr=($srcVolume.uncertain -or $dstVolume.uncertain)
        automatic_link_allowed=$false
    }
}

function Get-EventKey([string]$source,[string]$candidate,[string]$decision){
    $raw=($source.Trim().ToLowerInvariant()+"|"+$candidate.Trim().ToLowerInvariant()+"|"+$decision)
    $sha=[System.Security.Cryptography.SHA256]::Create()
    try{
        $bytes=$sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($raw))
        return [System.BitConverter]::ToString($bytes).Replace("-","").ToLowerInvariant()
    }finally{$sha.Dispose()}
}

function Write-TrialReward($selection,[string]$source){
    if($Demo){throw "Demo results cannot be scored"}
    $base=Join-Path $env:LOCALAPPDATA "BabyAI\Ginger"
    New-Item -ItemType Directory -Path $base -Force | Out-Null
    $ledger=Join-Path $base "ginger-trial-feedback.json"
    $events=@()
    if(Test-Path -LiteralPath $ledger -PathType Leaf){
        $existing=Get-Content -LiteralPath $ledger -Raw -Encoding UTF8 | ConvertFrom-Json
        if($existing.version -ne 1 -or $null -eq $existing.events){
            throw "Unrecognized feedback ledger; refuse to overwrite"
        }
        $events=@($existing.events)
    }
    $key=Get-EventKey $source $selection.candidate $selection.decision
    if(@($events | Where-Object {$_.key -eq $key}).Count -gt 0){
        Write-Host "Already reviewed this source/candidate pair. No duplicate reward."
        return
    }
    $existingScore=0
    foreach($evt in $events){$existingScore += [int]$evt.points}
    if($existingScore -le -75 -or @($events | Where-Object {[int]$_.points -le -100}).Count -gt 0){
        Write-Host "Trial is frozen because of negative feedback. Operator must review. No reward."
        return
    }
    Write-Host ""
    Write-Host "Human verification of UNIX's recommendation is required."
    Write-Host "Compare the ORIGINAL Ginger items, not just the OCR text."
    Write-Host "1 = recommendation is confirmed correct"
    Write-Host "2 = recommendation is confirmed wrong"
    Write-Host "0 = uncertain, skip (zero points)"
    $answer=(Read-Host "Verified result (1 / 2 / 0)").Trim()
    if($answer -eq "0"){Write-Host "Skipped. +0 points.";return}
    if($answer -notin @("1","2")){Write-Host "Not recorded.";return}
    $evidence=(Read-Host "Proof reference (your screenshot/check note, required)").Trim()
    if(-not $evidence -or $evidence.Length -gt 300){
        Write-Host "Evidence missing/too long. No reward."
        return
    }
    if($selection.decision -eq "reject"){
        $outcome=if($answer -eq "1"){"correct_reject"}else{"wrong_reject"}
        $points=if($answer -eq "1"){5}else{-20}
    }else{
        $outcome=if($answer -eq "1"){"correct_abstain"}else{"unnecessary_abstain"}
        $points=if($answer -eq "1"){3}else{-3}
    }
    $event=[pscustomobject]@{
        key=$key
        source_as_read=$source
        candidate_as_read=$selection.candidate
        decision=$selection.decision
        outcome=$outcome
        points=$points
        verifier="human_operator"
        evidence_reference=$evidence
        timestamp_utc=[DateTime]::UtcNow.ToString("o")
        no_erp_write=$true
    }
    $events+=,$event
    $payload=[pscustomobject]@{version=1;scope="trial_only_not_core_reward_ledger";events=@($events)}
    $temp=$ledger+".tmp"
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $ledger -Force
    $score=0
    foreach($evt in $events){$score += [int]$evt.points}
    Write-Host "Verified outcome: $outcome / awarded: $points / total: $score"
    if($score -le -75){
        Write-Host "SAFETY FREEZE: negative reward budget reached."
    }
    Write-Host "Trial feedback: $ledger"
    Write-Host "This is a local trial log, not the production SQLite reward ledger."
}

$data=Read-Report
$source=[string]$data.selected_row_visual_candidate
if([string]::IsNullOrWhiteSpace($source) -or
   $data.selected_row_color_band_detected -cne $true){
    throw "Selected Ginger supplier title was not visible. Open the line and repeat OCR."
}
$items=@($data.right_catalogue_candidates)
if($items.Count -eq 0){Write-Host "No candidates visible. No reward.";return}
Write-Host ""
Write-Host "===== UNIX / GINGER / CPU MATCHING TRIAL ====="
Write-Host "Selected supplier OCR: $source"
Write-Host "Source/item IDs are NOT verified. No auto-linking allowed."
if($Demo){Write-Host "DEMO DATA ONLY — no real Ginger activity or scoring."}
$assessments=[System.Collections.Generic.List[object]]::new()
foreach($candidate in $items){
    $text=[string]$candidate
    if(-not $text.Trim()){continue}
    if($text.Trim() -match '^(?i)(Товар|Цена|Товар Цена|Название в прайсе)\s*$'){continue}
    $item=Test-Candidate $source $text
    $assessments.Add($item)
    Write-Host ("[{0}] {1}" -f $assessments.Count,$item.candidate)
    Write-Host ("    UNIX: {0} ({1}); {2}" -f $item.decision,$item.classification,$item.reason)
}
if($assessments.Count -eq 0){Write-Host "No readable candidate text. +0 points.";return}
Write-Host "WARNING: image/OCR text may contain confusable Cyrillic/Latin letters."
Write-Host "No buttons clicked. Ginger and its server were not modified."
if($Demo -or $NonInteractive){return}
$chosen=(Read-Host "Which candidate should be reviewed? (number or 0 to exit)").Trim()
if($chosen -eq "0"){return}
$index=0
if(-not [int]::TryParse($chosen,[ref]$index) -or $index -lt 1 -or
   $index -gt $assessments.Count){
    Write-Host "No valid candidate selected. +0 points."
    return
}
Write-TrialReward -selection $assessments[$index-1] -source $source
)
            if($match.Success){$createdReports+=,$match.Groups[1].Value.Trim()}
        }
        if($createdReports.Count -ne 1){
            throw "Fresh Ginger OCR did not return exactly one report path. Refusing to use an older report."
        }
        $Report=[string]$createdReports[0]
        if(-not (Test-Path -LiteralPath $Report -PathType Leaf)){
            throw "OCR returned a report path that does not exist: $Report"
        }
        $fresh=Get-Item -LiteralPath $Report
        if($fresh.LastWriteTimeUtc -lt $captureStarted.AddSeconds(-5)){
            throw "The OCR report predates this capture. Refusing stale data."
        }
    }
    if(-not $Report.Trim()){
        $folders=@(Get-ChildItem -LiteralPath $downloads -Directory -Filter "ginger-cpu-test-*" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending)
        foreach($folder in $folders){
            $candidate=Join-Path $folder.FullName "ginger-cpu-report.json"
            if(Test-Path -LiteralPath $candidate -PathType Leaf){
                $Report=$candidate
                break
            }
        }
    }
    if(-not $Report -or -not (Test-Path -LiteralPath $Report -PathType Leaf)){
        throw "No Ginger report found. Use -CaptureFresh or -Report path to ginger-cpu-report.json."
    }
    $file=Get-Item -LiteralPath $Report
    if($file.Length -gt 4194304){throw "OCR report too large"}
    $document=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if($null -eq $document -or $document.source -ne "Ginger4 CPU OCR pilot" -or
       $document.read_only -cne $true -or $document.ginger_modified -cne $false -or
       $document.local_only -cne $true){
        throw "Report does not have valid Ginger read-only and local-only confirmation."
    }
    return $document
}

function Get-Volume([string]$title){
    $m=[regex]::Match($title,'(?i)(?<!\d)(\d{1,4})\s*(ml|mi|m!|m1|мл)(?![a-zа-я0-9])')
    if(-not $m.Success){return [pscustomobject]@{size=0;uncertain=$true}}
    return [pscustomobject]@{
        size=[int]$m.Groups[1].Value
        uncertain=($m.Groups[2].Value.ToLowerInvariant() -notin @("ml","мл"))
    }
}

function Get-Variant([string]$title){
    $m=[regex]::Match($title,'(?i)\b(intense|qahwa|elixir|extreme|absolu)\b')
    if($m.Success){return $m.Value.ToLowerInvariant()}
    return ""
}

function Get-Concentration([string]$title){
    $m=[regex]::Match($title,'(?i)\b(edp|edt|edc|extrait)\b')
    if($m.Success){return $m.Value.ToLowerInvariant()}
    return ""
}

function Get-Tester([string]$title){
    return [bool]([regex]::IsMatch($title,'(?i)(tester|testeur|тестер|тест)(?![а-яa-z])'))
}

function Test-Candidate([string]$source,[string]$candidate){
    $title=([regex]::Replace($candidate,'\s+\d{1,6}[.,]\d{2}\s*$','')).Trim()
    $firstSource=([regex]::Match($source,'^[^\s]+')).Value.ToLowerInvariant()
    $firstCandidate=([regex]::Match($title,'^[^\s]+')).Value.ToLowerInvariant()
    $srcVolume=Get-Volume $source
    $dstVolume=Get-Volume $title
    $srcConc=Get-Concentration $source
    $dstConc=Get-Concentration $title
    $srcVariant=Get-Variant $source
    $dstVariant=Get-Variant $title
    $classification="review"
    $reason="OCR is not proof of catalogue identity: compare the actual perfume, volume and packaging."
    $decision="abstain"
    if($firstSource -ne $firstCandidate){
        $classification="different_brand"
        $reason="Different first brand word."
        $decision="reject"
    }elseif((Get-Tester $source) -ne (Get-Tester $title)){
        $classification="tester_mismatch"
        $reason="Tester and regular bottle are different items."
        $decision="reject"
    }elseif($srcVariant -ne $dstVariant){
        $classification="variant_mismatch"
        $reason="Different fragrance variant."
        $decision="reject"
    }elseif($srcConc -and $dstConc -and $srcConc -ne $dstConc){
        $classification="concentration_mismatch"
        $reason="EDP/EDT concentration differs."
        $decision="reject"
    }elseif($srcVolume.size -ne 0 -and $dstVolume.size -ne 0 -and
            $srcVolume.size -ne $dstVolume.size){
        $classification="volume_mismatch"
        $reason="Bottle size differs."
        $decision="reject"
    }
    # Never mark any OCR title as an automatically linkable exact match.
    return [pscustomobject]@{
        candidate=$title
        classification=$classification
        decision=$decision
        reason=$reason
        uncertain_ocr=($srcVolume.uncertain -or $dstVolume.uncertain)
        automatic_link_allowed=$false
    }
}

function Get-EventKey([string]$source,[string]$candidate,[string]$decision){
    $raw=($source.Trim().ToLowerInvariant()+"|"+$candidate.Trim().ToLowerInvariant()+"|"+$decision)
    $sha=[System.Security.Cryptography.SHA256]::Create()
    try{
        $bytes=$sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($raw))
        return [System.BitConverter]::ToString($bytes).Replace("-","").ToLowerInvariant()
    }finally{$sha.Dispose()}
}

function Write-TrialReward($selection,[string]$source){
    if($Demo){throw "Demo results cannot be scored"}
    $base=Join-Path $env:LOCALAPPDATA "BabyAI\Ginger"
    New-Item -ItemType Directory -Path $base -Force | Out-Null
    $ledger=Join-Path $base "ginger-trial-feedback.json"
    $events=@()
    if(Test-Path -LiteralPath $ledger -PathType Leaf){
        $existing=Get-Content -LiteralPath $ledger -Raw -Encoding UTF8 | ConvertFrom-Json
        if($existing.version -ne 1 -or $null -eq $existing.events){
            throw "Unrecognized feedback ledger; refuse to overwrite"
        }
        $events=@($existing.events)
    }
    $key=Get-EventKey $source $selection.candidate $selection.decision
    if(@($events | Where-Object {$_.key -eq $key}).Count -gt 0){
        Write-Host "Already reviewed this source/candidate pair. No duplicate reward."
        return
    }
    $existingScore=0
    foreach($evt in $events){$existingScore += [int]$evt.points}
    if($existingScore -le -75 -or @($events | Where-Object {[int]$_.points -le -100}).Count -gt 0){
        Write-Host "Trial is frozen because of negative feedback. Operator must review. No reward."
        return
    }
    Write-Host ""
    Write-Host "Human verification of UNIX's recommendation is required."
    Write-Host "Compare the ORIGINAL Ginger items, not just the OCR text."
    Write-Host "1 = recommendation is confirmed correct"
    Write-Host "2 = recommendation is confirmed wrong"
    Write-Host "0 = uncertain, skip (zero points)"
    $answer=(Read-Host "Verified result (1 / 2 / 0)").Trim()
    if($answer -eq "0"){Write-Host "Skipped. +0 points.";return}
    if($answer -notin @("1","2")){Write-Host "Not recorded.";return}
    $evidence=(Read-Host "Proof reference (your screenshot/check note, required)").Trim()
    if(-not $evidence -or $evidence.Length -gt 300){
        Write-Host "Evidence missing/too long. No reward."
        return
    }
    if($selection.decision -eq "reject"){
        $outcome=if($answer -eq "1"){"correct_reject"}else{"wrong_reject"}
        $points=if($answer -eq "1"){5}else{-20}
    }else{
        $outcome=if($answer -eq "1"){"correct_abstain"}else{"unnecessary_abstain"}
        $points=if($answer -eq "1"){3}else{-3}
    }
    $event=[pscustomobject]@{
        key=$key
        source_as_read=$source
        candidate_as_read=$selection.candidate
        decision=$selection.decision
        outcome=$outcome
        points=$points
        verifier="human_operator"
        evidence_reference=$evidence
        timestamp_utc=[DateTime]::UtcNow.ToString("o")
        no_erp_write=$true
    }
    $events+=,$event
    $payload=[pscustomobject]@{version=1;scope="trial_only_not_core_reward_ledger";events=@($events)}
    $temp=$ledger+".tmp"
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $ledger -Force
    $score=0
    foreach($evt in $events){$score += [int]$evt.points}
    Write-Host "Verified outcome: $outcome / awarded: $points / total: $score"
    if($score -le -75){
        Write-Host "SAFETY FREEZE: negative reward budget reached."
    }
    Write-Host "Trial feedback: $ledger"
    Write-Host "This is a local trial log, not the production SQLite reward ledger."
}

$data=Read-Report
$source=[string]$data.selected_row_visual_candidate
if([string]::IsNullOrWhiteSpace($source) -or
   $data.selected_row_color_band_detected -cne $true){
    throw "Selected Ginger supplier title was not visible. Open the line and repeat OCR."
}
$items=@($data.right_catalogue_candidates)
if($items.Count -eq 0){Write-Host "No candidates visible. No reward.";return}
Write-Host ""
Write-Host "===== UNIX / GINGER / CPU MATCHING TRIAL ====="
Write-Host "Selected supplier OCR: $source"
Write-Host "Source/item IDs are NOT verified. No auto-linking allowed."
if($Demo){Write-Host "DEMO DATA ONLY — no real Ginger activity or scoring."}
$assessments=[System.Collections.Generic.List[object]]::new()
foreach($candidate in $items){
    $text=[string]$candidate
    if(-not $text.Trim()){continue}
    if($text.Trim() -match '^(?i)(Товар|Цена|Товар Цена|Название в прайсе)\s*$'){continue}
    $item=Test-Candidate $source $text
    $assessments.Add($item)
    Write-Host ("[{0}] {1}" -f $assessments.Count,$item.candidate)
    Write-Host ("    UNIX: {0} ({1}); {2}" -f $item.decision,$item.classification,$item.reason)
}
if($assessments.Count -eq 0){Write-Host "No readable candidate text. +0 points.";return}
Write-Host "WARNING: image/OCR text may contain confusable Cyrillic/Latin letters."
Write-Host "No buttons clicked. Ginger and its server were not modified."
if($Demo -or $NonInteractive){return}
$chosen=(Read-Host "Which candidate should be reviewed? (number or 0 to exit)").Trim()
if($chosen -eq "0"){return}
$index=0
if(-not [int]::TryParse($chosen,[ref]$index) -or $index -lt 1 -or
   $index -gt $assessments.Count){
    Write-Host "No valid candidate selected. +0 points."
    return
}
Write-TrialReward -selection $assessments[$index-1] -source $source
