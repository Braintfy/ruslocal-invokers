# Русификатор Invokers: установка перевода в версию игры для ПК.
#
# The computer does everything here, unlike the Android helper next to it: the game's own two language
# files are already on this machine, so the bundled tool composes the Russian file locally and this
# script puts it in place. Nothing is uploaded anywhere.
#
# The catalog ships inside the archive rather than being fetched every run. It is 35 MB, and pulling
# that over plain HTTP is the least reliable step in the whole flow — downloads abort halfway often
# enough that making the tool depend on one would be the main reason it fails. A newer catalog is
# still fetched when the network allows, and the bundled copy is the floor, not the ceiling.
#
# The original is kept outside this folder, under %LOCALAPPDATA%\InvokersRu, so deleting the archive
# or unpacking it elsewhere does not strand anyone with a patched file and no way back.

param(
    # install | restore | redownload. Omitted means the interactive menu.
    [string]$Action,
    # Where the game keeps its i18n folder. Omitted means find it.
    [string]$GameDir,
    # Skip the "press Enter" pause, for running this from another script.
    [switch]$Quiet
)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
# Windows PowerShell still defaults to TLS 1.0 on some machines, and GitHub refuses that.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$Target  = 'dl_uk_UA.bin'
$English = 'dl_en_US.bin'
$CatalogUrl = 'https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main/translations/ru_RU.jsonl'
# A truncated catalog would compose a half-English file without any error, so the floor is generous.
$CatalogMinBytes = 20000000

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Overridable so the flow can be exercised without the game installed; the defaults are what players use.
if (-not $GameDir) { $GameDir = $env:INVOKERSRU_GAME_DIR }
$Cli = $env:INVOKERSRU_CLI
if (-not $Cli) { $Cli = Join-Path $Here 'InvokersRu.Cli.exe' }
$Bundled = Join-Path $Here 'ru_RU.jsonl'

$StateRoot = $env:INVOKERSRU_STATE
if (-not $StateRoot) {
    $localAppData = $env:LOCALAPPDATA
    if (-not $localAppData) { $localAppData = Join-Path $HOME '.local/share' }
    $StateRoot = Join-Path $localAppData 'InvokersRu'
}
$Work      = Join-Path $StateRoot 'work'
$BackupDir = Join-Path $StateRoot 'backup'
$StateFile = Join-Path $StateRoot 'state.txt'
$LogFile   = Join-Path $StateRoot 'patcher.log'

New-Item -ItemType Directory -Force -Path $StateRoot, $Work, $BackupDir | Out-Null

function Say([string]$text) { Write-Host $text }
function Head([string]$text) { Write-Host ''; Write-Host "== $text =="; Write-Host '' }
function Log([string]$text) {
    "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text | Out-File -FilePath $LogFile -Append -Encoding utf8
}

function Finish([int]$code) {
    Write-Host ''
    if (-not $Quiet -and -not $Action) {
        Write-Host 'Нажмите Enter, чтобы закрыть окно.'
        [void](Read-Host)
    }
    exit $code
}

function Die([string]$text) {
    Write-Host ''
    Write-Host "НЕ ПОЛУЧИЛОСЬ: $text"
    Log "FATAL: $text"
    Finish 1
}

function Sha([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToUpper()
}

# The game is a Unity player, so its writable data sits under LocalLow in a folder named after the
# company and the product. Those names have changed spelling between builds, so the English file is
# what identifies the directory rather than the path.
function Find-GameDir {
    if ($GameDir) {
        if (Test-Path -LiteralPath (Join-Path $GameDir $English)) { return $GameDir }
        Die "В указанной папке нет $English : $GameDir"
    }

    $roots = @()
    if ($env:USERPROFILE) { $roots += (Join-Path $env:USERPROFILE 'AppData\LocalLow') }
    if ($env:LOCALAPPDATA) { $roots += ($env:LOCALAPPDATA + 'Low') }
    $roots = @($roots | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ })

    $found = @()
    foreach ($root in $roots) {
        # Two levels down is company\product, and the i18n folder sits inside it.
        foreach ($company in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            foreach ($product in (Get-ChildItem -LiteralPath $company.FullName -Directory -ErrorAction SilentlyContinue)) {
                $candidate = Join-Path $product.FullName 'i18n'
                if (Test-Path -LiteralPath (Join-Path $candidate $English)) { $found += $candidate }
            }
        }
    }

    $found = @($found | Select-Object -Unique)
    if ($found.Count -eq 1) { return $found[0] }
    if ($found.Count -gt 1) {
        Say 'Найдено несколько папок с данными игры:'
        $found | ForEach-Object { Say "  $_" }
        Die 'Русификатор не станет угадывать, какую из них менять. Укажите нужную: -GameDir "полный\путь"'
    }
    Die @"
Данные игры Invokers на этом компьютере не найдены.

Проверьте, что игра установлена и запускалась хотя бы один раз — языковые файлы
она скачивает при первом запуске, а не хранит внутри себя.

Искали здесь:
$(if ($roots) { $roots -join "`n" } else { '  (папки LocalLow нет — это точно Windows?)' })

Если игра стоит в необычном месте, укажите папку вручную:
  .\install-pc.ps1 -Action install -GameDir "C:\путь\до\i18n"
"@
}

function Require-Cli {
    if (-not (Test-Path -LiteralPath $Cli)) {
        Die 'Рядом со скриптом нет InvokersRu.Cli.exe. Распакуйте архив целиком, а не по одному файлу.'
    }
    & $Cli help *> $null
    if ($LASTEXITCODE -ne 0) {
        Die 'Встроенный обработчик файлов игры не запускается. Скачайте архив заново.'
    }
}

# A catalog is usable only if it is whole. Size alone would accept a file cut off mid-line, so the last
# line has to parse as one of the records too.
function Test-Catalog([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    if ((Get-Item -LiteralPath $path).Length -lt $CatalogMinBytes) { return $false }
    try {
        $last = Get-Content -LiteralPath $path -Tail 1 -ErrorAction Stop
        if (-not $last) { return $false }
        return ($last | ConvertFrom-Json).id -ne $null
    } catch { return $false }
}

# curl.exe ships with Windows 10 1803 and later and survives a 35 MB transfer far better than
# Invoke-WebRequest, which has no resume and aborts often. Invoke-WebRequest stays as the fallback for
# older machines.
function Fetch-File([string]$url, [string]$destination) {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -fsSL --retry 3 --retry-delay 2 --max-time 900 -o $destination $url 2>$null
        if ($LASTEXITCODE -eq 0) { return $true }
    }
    try {
        Invoke-WebRequest -Uri $url -OutFile $destination -UseBasicParsing -ErrorAction Stop
        return $true
    } catch {
        Log "download failed: $($_.Exception.Message)"
        return $false
    }
}

function Get-Catalog {
    $cached = Join-Path $Work 'ru_RU.jsonl'
    $fresh  = Join-Path $Work 'ru_RU.jsonl.new'
    Remove-Item -LiteralPath $fresh -Force -ErrorAction SilentlyContinue

    Say 'Проверяю, нет ли свежего перевода…'
    if (Fetch-File $CatalogUrl $fresh) {
        if (Test-Catalog $fresh) {
            Move-Item -LiteralPath $fresh -Destination $cached -Force
            Say 'Взят свежий перевод из репозитория.'
            return $cached
        }
        Say 'Скачанный перевод оказался неполным — он не используется.'
        Log 'downloaded catalog failed the integrity check'
    } else {
        Say 'Скачать не удалось.'
    }
    Remove-Item -LiteralPath $fresh -Force -ErrorAction SilentlyContinue

    if (Test-Catalog $cached) { Say 'Использую ранее скачанный перевод.'; return $cached }
    if (Test-Catalog $Bundled) { Say 'Использую перевод из архива.'; return $Bundled }
    Die 'Перевод не найден ни в архиве, ни в загрузках. Скачайте архив заново.'
}

# Written next to the target and moved into place, so an interrupted run cannot leave the game with a
# half-written file. The copy is verified by hash before it replaces anything.
function Install-Atomic([string]$source, [string]$destination) {
    $temp = "$destination.invokersru.tmp"
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $source -Destination $temp -Force -ErrorAction Stop
    if ((Sha $temp) -ne (Sha $source)) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        Die 'Проверка копии не прошла — файл игры не тронут.'
    }
    Move-Item -LiteralPath $temp -Destination $destination -Force -ErrorAction Stop
}

function Test-GameRunning {
    foreach ($name in @('Invokers', 'invokers')) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

function Read-State([string]$key) {
    if (-not (Test-Path -LiteralPath $StateFile)) { return $null }
    foreach ($line in (Get-Content -LiteralPath $StateFile -ErrorAction SilentlyContinue)) {
        if ($line -like "$key=*") { return $line.Substring($key.Length + 1) }
    }
    return $null
}

function Invoke-Install {
    Head 'Установка перевода'
    $dir = Find-GameDir
    Say "Данные игры: $dir"
    Log "install into $dir"

    $english = Join-Path $dir $English
    $target  = Join-Path $dir $Target

    if (-not (Test-Path -LiteralPath $target)) {
        Die @"
Украинский языковой файл ещё не загружен.

Запустите игру, зайдите в настройки, выберите УКРАИНСКИЙ язык, дождитесь
загрузки и полностью закройте игру. Русский текст подставляется в украинскую
ячейку — она единственная кириллическая в игре.
"@
    }
    if (Test-GameRunning) { Die 'Игра сейчас запущена. Полностью закройте Invokers и повторите.' }

    Require-Cli
    $catalog = Get-Catalog

    # The original is captured before anything is built, and only while it is still the game's own file:
    # once a translated file sits there, copying it would overwrite the real original with a patched one.
    $targetSha = Sha $target
    $installedBefore = Read-State 'installed'
    if ($installedBefore -eq $targetSha) {
        Say 'Перевод уже установлен — оригинал был сохранён раньше.'
    } else {
        $backup = Join-Path $BackupDir "original-$targetSha.bin"
        if (-not (Test-Path -LiteralPath $backup)) {
            Copy-Item -LiteralPath $target -Destination $backup -Force -ErrorAction Stop
            Log "backup $backup"
        }
        Set-Content -LiteralPath $StateFile -Value @("original=$targetSha") -Encoding utf8
        Say 'Оригинал сохранён.'
    }
    $originalSha = Read-State 'original'

    Say 'Собираю русский файл…'
    $built  = Join-Path $Work 'dl_uk_UA.ru.bin'
    $report = Join-Path $Work 'report.json'
    # The tool refuses to overwrite either of its outputs, so a leftover from a previous run would fail
    # the build with nothing on screen to explain why.
    Remove-Item -LiteralPath $built, $report -Force -ErrorAction SilentlyContinue
    # Every draft is applied. Most needs-review flags mean identical English appears on several screens,
    # which is a wording nuance; mechanically broken strings are rejected at import and never ship.
    & $Cli build --english $english --base $target --translations $catalog --output $built `
        --report $report --include-draft --raw --per-locale-content-version *>> $LogFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $built)) {
        Die "Не удалось собрать перевод для этой версии игры. Возможно, игра обновилась. Подробности: $LogFile"
    }

    Install-Atomic $built $target
    $installed = Sha $target
    Set-Content -LiteralPath $StateFile -Value @("original=$originalSha", "installed=$installed") -Encoding utf8
    Log "installed $installed"

    Head 'Готово'
    Say 'Перевод установлен.'
    Say ''
    Say 'ВАЖНО: не открывайте выбор языка в настройках игры. При выборе любого языка'
    Say 'клиент заново скачает языковой файл с сервера и сотрёт перевод.'
    Finish 0
}

function Invoke-Restore {
    Head 'Восстановление оригинала'
    $dir = Find-GameDir
    $target = Join-Path $dir $Target

    $backup = $null
    $original = Read-State 'original'
    if ($original) {
        $candidate = Join-Path $BackupDir "original-$original.bin"
        if (Test-Path -LiteralPath $candidate) { $backup = $candidate }
    }
    if (-not $backup) {
        $backup = Get-ChildItem -LiteralPath $BackupDir -Filter 'original-*.bin' -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $backup) {
        Die @"
Сохранённого оригинала нет.

Это не тупик: откройте в игре настройки и выберите любой язык. Клиент скачает
языковой файл заново и вернёт оригинальный текст.
"@
    }
    if (Test-GameRunning) { Die 'Игра сейчас запущена. Полностью закройте Invokers и повторите.' }

    Install-Atomic $backup $target
    Remove-Item -LiteralPath $StateFile -Force -ErrorAction SilentlyContinue
    Log 'restored'
    Head 'Готово'
    Say 'Оригинальный текст возвращён.'
    Finish 0
}

function Show-Redownload {
    Head 'Как заставить игру скачать оригинал заново'
    Say 'Если сохранённого оригинала нет, игра вернёт его сама:'
    Say ''
    Say '  1. Запустите Invokers.'
    Say '  2. Откройте настройки и выберите любой язык.'
    Say '  3. Дождитесь загрузки — клиент перекачает языковой файл с сервера.'
    Say ''
    Say 'Тот же приём стирает и установленный перевод, поэтому после установки'
    Say 'выбор языка открывать нельзя.'
    Finish 0
}

if (-not $Action) {
    Head 'Русификатор Invokers — версия для ПК'
    Say 'Неофициальный любительский перевод. Проект не связан с HitZone Inc.'
    Say 'Изменяется один файл кэша локализации, сама игра не трогается.'
    Say ''
    Say '  [1] Установить перевод'
    Say '  [2] Восстановить оригинал'
    Say '  [3] Игра сама скачает оригинал (если резервной копии нет)'
    Say '  [0] Выход'
    Say ''
    switch ((Read-Host -Prompt 'Что сделать').Trim()) {
        '1' { $Action = 'install' }
        '2' { $Action = 'restore' }
        '3' { $Action = 'redownload' }
        default { Finish 0 }
    }
}

switch ($Action.ToLower()) {
    'install'    { Invoke-Install }
    'restore'    { Invoke-Restore }
    'redownload' { Show-Redownload }
    default {
        Say "Неизвестное действие: $Action"
        Say 'Допустимо: install, restore, redownload'
        Finish 2
    }
}
