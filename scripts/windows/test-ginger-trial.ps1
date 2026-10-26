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

# Russian text is UTF-8 with BOM for Windows PowerShell 5.1.
# Machine-readable status codes and JSON field names remain unchanged.
function Get-RussianDecision([string]$decision) {
    switch($decision) {
        "reject" { return "ОТКЛОНИТЬ — найдено несовпадение" }
        "abstain" { return "НУЖНА ПРОВЕРКА — данных пока недостаточно" }
        "link" { return "СОПОСТАВИТЬ (в этом тесте запрещено)" }
        default { return "НЕИЗВЕСТНО — остановка" }
    }
}
function Get-RussianOutcome([string]$outcome) {
    switch($outcome) {
        "correct_reject" { return "Правильно отклонил неподходящий товар" }
        "wrong_reject" { return "Ошибочно отклонил товар" }
        "correct_abstain" { return "Правильно отправил на ручную проверку" }
        "unnecessary_abstain" { return "Напрасно отправил на ручную проверку" }
        default { return "Неизвестная оценка" }
    }
}
function Write-RussianOcrMessage([string]$line) {
    if($line.StartsWith("CPU OCR completed: ",[StringComparison]::OrdinalIgnoreCase)){
        Write-Host ("OCR завершён. Файл отчёта: " + $line.Substring("CPU OCR completed: ".Length))
    }elseif($line.StartsWith("Switching from PowerShell to Ginger4 window")){
        Write-Host "Переключаюсь на окно Ginger..."
    }elseif($line.StartsWith("Left lines: ")){
        Write-Host ("Сведения OCR: " + $line)
    }elseif($line.StartsWith("Read-only: ")){
        Write-Host "Режим только чтения: товары и сервер Ginger не изменены."
    }elseif($line.StartsWith("Review report for sensitive")){
        Write-Host "Перед отправкой отчёта проверь, нет ли в нём служебных данных."
    }else{
        Write-Host $line
    }
}

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
    $downloads = Join-Path $env:USERPROFILE "Downloads"
    if($CaptureFresh -and $Report.Trim()){
        throw "Укажи только один вариант: -CaptureFresh (новый снимок) или -Report (готовый отчёт)."
    }
    if($CaptureFresh){
        $ocr=Join-Path $PSScriptRoot "test-ginger-cpu-ocr.ps1"
        if(-not (Test-Path -LiteralPath $ocr -PathType Leaf)){
            throw "Рядом со скриптом отсутствует test-ginger-cpu-ocr.ps1. Скачай его в ту же папку."
        }
        Write-Host "Считываю текущее окно Ginger (локально, без изменения товаров)..."
        $captureStarted=[DateTime]::UtcNow
        # IMPORTANT: do not leak child stdout into this function's output
        # stream. It must return ONLY one JSON object to the caller.
        $ocrOutput=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ocr -CaptureSupplierGrid 2>&1)
        $ocrExit=$LASTEXITCODE
        foreach($message in $ocrOutput){Write-RussianOcrMessage ([string]$message)}
        if($ocrExit -ne 0){
            throw "Не удалось распознать свежий экран Ginger. Старый отчёт и начисление очков запрещены."
        }
        $createdReports=@()
        $prefix="CPU OCR completed: "
        foreach($message in $ocrOutput){
            $line=[string]$message
            if($line.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){
                $createdReports+=,$line.Substring($prefix.Length).Trim()
            }
        }
        if($createdReports.Count -ne 1 -or
           [string]::IsNullOrWhiteSpace([string]$createdReports[0])){
            throw "OCR не вернул один корректный путь к новому отчёту. Старые данные использовать нельзя."
        }
        $Report=([string]$createdReports[0]).Trim()
        if(-not (Test-Path -LiteralPath $Report -PathType Leaf)){
            throw "OCR сообщил путь к отчёту, но файл не существует."
        }
        $fresh=Get-Item -LiteralPath $Report
        if($fresh.LastWriteTimeUtc -lt $captureStarted.AddSeconds(-5)){
            throw "Отчёт создан раньше текущего снимка. Использование устаревших данных запрещено."
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
        throw "Отчёт Ginger не найден. Запусти с -CaptureFresh или укажи -Report и путь к ginger-cpu-report.json."
    }
    $file=Get-Item -LiteralPath $Report
    if($file.Length -gt 4194304){throw "Слишком большой OCR-отчёт (больше 4 МБ)."}
    $script:CurrentReportPath=$file.FullName
    $script:CurrentReportSha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $document=Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if($null -eq $document -or $document.source -ne "Ginger4 CPU OCR pilot" -or
       $document.read_only -cne $true -or $document.ginger_modified -cne $false -or
       $document.local_only -cne $true){
        throw "Отчёт не подтверждает безопасный локальный режим чтения Ginger."
    }
    $required=@("selected_row_visual_candidate","selected_row_color_band_detected","right_catalogue_candidates")
    foreach($field in $required){
        if($null -eq $document.PSObject.Properties[$field]){
            throw "В отчёте нет данных о выбранной строке. Скачай обновлённый OCR-скрипт и запусти -CaptureFresh."
        }
    }
    if($document.selected_row_color_band_detected -cne $true -or
       [string]::IsNullOrWhiteSpace([string]$document.selected_row_visual_candidate)){
        throw "Не удалось прочитать выделенный товар поставщика. Выдели одну строку в Ginger и повтори запуск."
    }
    if($null -eq $document.right_catalogue_candidates -or
       $document.right_catalogue_candidates -isnot [array]){
        throw "Список товаров справа в отчёте повреждён. Начисление очков остановлено."
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
    $reason="OCR может ошибаться: проверь сам аромат, объём и упаковку в Ginger."
    $decision="abstain"
    if($firstSource -ne $firstCandidate){
        $classification="different_brand"
        $reason="Бренд не совпадает."
        $decision="reject"
    }elseif((Get-Tester $source) -ne (Get-Tester $title)){
        $classification="tester_mismatch"
        $reason="Тестер и обычный флакон — разные товары."
        $decision="reject"
    }elseif($srcVariant -ne $dstVariant){
        $classification="variant_mismatch"
        $reason="Отличается версия аромата (например, INTENSE или ELIXIR)."
        $decision="reject"
    }elseif($srcConc -and $dstConc -and $srcConc -ne $dstConc){
        $classification="concentration_mismatch"
        $reason="Не совпадает концентрация (EDP/EDT)."
        $decision="reject"
    }elseif($srcVolume.size -ne 0 -and $dstVolume.size -ne 0 -and
            $srcVolume.size -ne $dstVolume.size){
        $classification="volume_mismatch"
        $reason="Не совпадает объём флакона."
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

function Test-ReviewNote([string]$note){
    # This is a sanity gate, NOT independent verification of human truth.
    if([string]::IsNullOrWhiteSpace($note)){return $false}
    $text=$note.Trim()
    if($text.Length -lt 25 -or $text.Length -gt 500){return $false}
    if(@($text -split '\s+' | Where-Object {$_}).Count -lt 4){return $false}
    if($text -match '^(?i)(required|ok|yes|test|checked|correct|proof|проверил|ок|да|верно)\W*$'){
        return $false
    }
    # Ask for a concrete identifying observation, not a generic approval.
    if($text -notmatch '(?i)(tester|testeur|тестер|флакон|объем|объём|ml|мл|edp|edt|бренд|марка|brand|variant|концентрац|название|аромат|упаковк|volume|bottle|name)'){
        return $false
    }
    return $true
}

function Test-TrustedTrialEvent($evt){
    # Earlier v1 feedback such as evidence_reference="required" is still
    # retained for audit, but its positive score is NOT trusted.
    if($null -eq $evt){return $false}
    foreach($field in @("evidence_level","report_sha256","review_note","points")){
        if($null -eq $evt.PSObject.Properties[$field]){return $false}
    }
    if($evt.evidence_level -cne "human_attested_with_report_hash_v2"){return $false}
    if(([string]$evt.report_sha256) -notmatch '^[a-f0-9]{64}$'){return $false}
    if(-not (Test-ReviewNote ([string]$evt.review_note))){return $false}
    return $true
}

function Write-TrialReward($selection,[string]$source){
    if($Demo){throw "За демонстрацию очки не начисляются."}
    if(-not $script:CurrentReportPath -or
       ([string]$script:CurrentReportSha256) -notmatch '^[a-f0-9]{64}$'){
        throw "Отсутствует хеш исходного OCR-отчёта. Оценка не принимается."
    }
    $base=Join-Path $env:LOCALAPPDATA "BabyAI\Ginger"
    New-Item -ItemType Directory -Path $base -Force | Out-Null
    $ledger=Join-Path $base "ginger-trial-feedback.json"
    $events=@()
    if(Test-Path -LiteralPath $ledger -PathType Leaf){
        $existing=Get-Content -LiteralPath $ledger -Raw -Encoding UTF8 | ConvertFrom-Json
        if($existing.version -notin @(1,2) -or $null -eq $existing.events){
            throw "Неизвестный формат журнала оценок. Перезапись запрещена."
        }
        $events=@($existing.events)
    }
    $key=Get-EventKey $source $selection.candidate $selection.decision
    $trustedEvents=@($events | Where-Object {Test-TrustedTrialEvent $_})
    if(@($trustedEvents | Where-Object {$_.key -eq $key}).Count -gt 0){
        Write-Host "Эта пара товаров уже оценивалась. Повторная награда не начисляется."
        return
    }
    $existingScore=0
    $critical=$false
    foreach($evt in $events){
        $points=[int]$evt.points
        # Keep old negative outcomes conservative; drop unsubstantiated
        # positive v1 scores from the effective safety budget.
        if($points -lt 0 -or (Test-TrustedTrialEvent $evt)){
            $existingScore+=$points
        }
        if($points -le -100){$critical=$true}
    }
    if($existingScore -le -75 -or $critical){
        Write-Host "Тест заблокирован из-за ошибок. Нужна проверка оператором, новые очки не начисляются."
        return
    }
    Write-Host ""
    Write-Host "Сверь выбранную позицию с ОРИГИНАЛЬНЫМИ строками в Ginger, не только с распознанным текстом."
    Write-Host "1 — UNIX правильно решил НЕ сопоставлять этот товар автоматически."
    Write-Host "2 — UNIX ошибся в оценке кандидата."
    Write-Host "0 — не уверен / не проверял (0 очков)."
    $answer=(Read-Host "Твоя оценка (1, 2 или 0)").Trim()
    if($answer -eq "0"){Write-Host "Проверка пропущена. Начислено 0 очков.";return}
    if($answer -notin @("1","2")){Write-Host "Неверный выбор. Оценка не сохранена.";return}
    Write-Host "Объясни, что именно проверил в Ginger: бренд, тестер, объём или концентрацию."
    Write-Host "Напиши не меньше 25 символов и 4 слов. Например: Это тестер, а поставщик указал обычный флакон 100 мл."
    $note=(Read-Host "Что именно ты проверил?").Trim()
    if(-not (Test-ReviewNote $note)){
        Write-Host "Заглушка вместо объяснения не принимается. Начислено 0 очков."
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
        verifier="human_operator_attestation"
        evidence_level="human_attested_with_report_hash_v2"
        review_note=$note
        report_sha256=$script:CurrentReportSha256
        timestamp_utc=[DateTime]::UtcNow.ToString("o")
        no_erp_write=$true
    }
    $events+=,$event
    $payload=[pscustomobject]@{version=2;scope="trial_only_not_core_reward_ledger";events=@($events)}
    $temp=$ledger+".tmp"
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $ledger -Force
    $score=$existingScore+$points
    Write-Host ("Оценка: {0}. Баллы: {1}; итоговый счёт: {2}" -f (Get-RussianOutcome $outcome),$points,$score)
    Write-Host "SHA-256 исходного отчёта: $($script:CurrentReportSha256)"
    Write-Host "Старые неподтверждённые положительные очки остались в истории, но исключены из счёта."
    if($score -le -75){Write-Host "ВНИМАНИЕ! Тест заблокирован из-за отрицательного счёта."}
    Write-Host "Журнал оценок: $ledger"
    Write-Host "Примечание: твоё объяснение не заменяет независимую проверку идентификатора товара."
    Write-Host "Это локальный тестовый журнал, а не основная база наград UNIX."
}

$data=Read-Report
$source=[string]$data.selected_row_visual_candidate
if([string]::IsNullOrWhiteSpace($source) -or
   $data.selected_row_color_band_detected -cne $true){
    throw "Не удалось определить выбранный товар. Выдели строку в Ginger и повтори OCR."
}
$items=@($data.right_catalogue_candidates)
if($items.Count -eq 0){Write-Host "Справа не видно подходящих строк для оценки. Очки не начислены.";return}
Write-Host ""
Write-Host "===== UNIX — ПРОВЕРКА СОПОСТАВЛЕНИЯ GINGER ====="
Write-Host "Товар поставщика (распознано с экрана): $source"
Write-Host "ВНИМАНИЕ: идентификаторы товаров не подтверждены. Автосопоставление выключено."
if($Demo){Write-Host "ДЕМО: пример без чтения Ginger и начисления очков."}
$assessments=[System.Collections.Generic.List[object]]::new()
foreach($candidate in $items){
    $text=[string]$candidate
    if(-not $text.Trim()){continue}
    if($text.Trim() -match '^(?i)(Товар|Цена|Товар Цена|Название в прайсе)\s*$'){continue}
    $item=Test-Candidate $source $text
    $assessments.Add($item)
    Write-Host ("[{0}] {1}" -f $assessments.Count,$item.candidate)
    Write-Host ("    Решение UNIX: {0}. Причина: {1}" -f (Get-RussianDecision $item.decision),$item.reason)
}
if($assessments.Count -eq 0){Write-Host "Не удалось прочитать товары справа. +0 очков.";return}
Write-Host "ВНИМАНИЕ: распознавание иногда путает латинские и русские буквы."
Write-Host "Безопасный режим: никаких кликов, Ginger и его сервер не изменены."
if($Demo -or $NonInteractive){return}
$chosen=(Read-Host "Номер товара для проверки (1, 2, 3... или 0 — выход)").Trim()
if($chosen -eq "0"){return}
$index=0
if(-not [int]::TryParse($chosen,[ref]$index) -or $index -lt 1 -or
   $index -gt $assessments.Count){
    Write-Host "Неверный номер товара. +0 очков."
    return
}
Write-TrialReward -selection $assessments[$index-1] -source $source
