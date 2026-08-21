# Русификатор Invokers: установка перевода на Android с компьютера.
#
# The computer is only a courier. It cannot compose anything itself — that needs the game's own two
# language files plus the catalog — so it copies those into the patcher app's directory, where adb
# may write and the app may read, asks the app to build the Russian file, and copies the result back
# into the game. The only tool required is adb, and this script fetches even that on its own.

# Left on Continue on purpose: PowerShell 5.1 turns anything a native tool writes to stderr into a
# terminating error when this is Stop, and adb is chatty on stderr even when it succeeds. The few
# calls that must not fail quietly carry -ErrorAction Stop themselves.
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# Windows PowerShell still defaults to TLS 1.0 on some machines, and GitHub refuses that.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$PkgGame   = 'hitzone.anima.spirit.guardians'
$PkgApp    = 'ru.invokers.patcher'
$GameDir   = "/sdcard/Android/data/$PkgGame/files/i18n"
$Bridge    = "/sdcard/Android/data/$PkgApp/files/bridge"
$Target    = 'dl_uk_UA.bin'
$English   = 'dl_en_US.bin'
$Stamp     = 'dl_uk_UA.bin.ver'
$CatalogUrl = 'https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main/translations/ru_RU.jsonl'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Work = Join-Path $Here 'data'
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# The original and the record of what was installed live on the phone, not next to this script. That
# way a player who re-downloads the archive, or runs it from another computer, can still put the
# original text back instead of being stranded with a patched file and no way home.
$PhoneBackup = "$Bridge/backup/original.bin"
$PhoneState  = "$Bridge/backup/state.txt"

$Adb = $null
$Device = $null

function Say([string]$text) { Write-Host $text }
function Head([string]$text) { Write-Host ""; Write-Host "== $text ==" ; Write-Host "" }

function Finish([int]$code) {
    Write-Host ""
    Write-Host "Нажмите Enter, чтобы закрыть окно."
    [void](Read-Host)
    exit $code
}

function Die([string]$text) {
    Write-Host ""
    Write-Host "НЕ ПОЛУЧИЛОСЬ: $text"
    Finish 1
}

function Ask([string]$prompt) { return (Read-Host -Prompt $prompt).Trim() }

function ShaLocal([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLower()
}

function AdbRaw { & $Adb @args 2>$null }
function AdbDev { & $Adb -s $Device @args 2>$null }
function Sh([string]$command) {
    $out = & $Adb -s $Device shell $command 2>$null
    if ($null -eq $out) { return '' }
    return (($out -join "`n") -replace "`r", '')
}

function EnsureAdb {
    $bundled = Join-Path $Here 'platform-tools\adb.exe'
    if (Test-Path $bundled) { $script:Adb = $bundled; return }
    $found = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($found) { $script:Adb = $found.Source; return }

    Say "Утилита связи с телефоном (adb) не найдена — скачиваю её с сайта Google."
    Say "Это около 15 МБ, один раз."
    $zip = Join-Path $Here 'platform-tools.zip'
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -ErrorAction Stop -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $zip
        Expand-Archive -ErrorAction Stop -Path $zip -DestinationPath $Here -Force
        Remove-Item $zip -Force
    } catch {
        Die "Не удалось скачать adb. Проверьте интернет и антивирус."
    }
    if (-not (Test-Path $bundled)) { Die "Скачали, но adb на месте не оказалось." }
    $script:Adb = $bundled
    Say "Готово."
}

# The limit is a parameter because the emulator probe should give up in seconds and move on to asking
# for the port, while a cable or Wi-Fi connection deserves the full two minutes.
function WaitForDevice([int]$Limit = 60) {
    for ($i = 0; $i -lt $Limit; $i++) {
        $lines = (AdbRaw devices) -split "`n" | ForEach-Object { ($_ -replace "`r", '').Trim() } |
                 Where-Object { $_ -and $_ -notmatch '^List of devices' }
        if ($lines -match 'unauthorized') {
            Say "Телефон подключён, но не разрешил отладку."
            Say "Посмотрите на экран телефона: там окно «Разрешить отладку по USB?»."
            Say "Поставьте галочку «Всегда разрешать» и нажмите «Разрешить»."
        }
        foreach ($line in $lines) {
            $parts = $line -split '\s+'
            if ($parts.Count -ge 2 -and $parts[1] -eq 'device') { $script:Device = $parts[0]; return $true }
        }
        Start-Sleep -Seconds 2
        if ($i -gt 0 -and $i % 5 -eq 0) { Say "Жду телефон…" }
    }
    return $false
}

# Android emulators run on this very computer and expose adb on a local port, so there is no cable, no
# pairing code and no prompt on a phone screen to accept. These are the defaults the common emulators
# ship with; BlueStacks assigns one per instance and prints it in its own settings.
$EmulatorPorts = @(5555, 5556, 5565, 5575, 5585, 5595, 62001, 62025, 21503, 5554)

# BlueStacks writes the port of every instance into its shared config, which beats guessing.
function BlueStacksPorts {
    $found = @()
    foreach ($conf in @("$env:ProgramData\BlueStacks_nxt\bluestacks.conf",
                        "$env:ProgramData\BlueStacks\bluestacks.conf")) {
        if (-not (Test-Path -LiteralPath $conf)) { continue }
        foreach ($line in (Get-Content -LiteralPath $conf -ErrorAction SilentlyContinue)) {
            if ($line -match 'adb_port\s*=\s*"?(\d+)"?') { $found += [int]$Matches[1] }
        }
    }
    return ($found | Select-Object -Unique)
}

function ConnectEmulator {
    Say ""
    Say "Ищу эмулятор на этом компьютере…"
    $ports = @(BlueStacksPorts) + $EmulatorPorts | Select-Object -Unique
    foreach ($port in $ports) {
        $out = (AdbRaw connect "127.0.0.1:$port") -join ' '
        if ($out -match 'connected to') { Say "Отозвался порт $port." }
    }
    if (WaitForDevice 6) { return $true }

    Say ""
    Say "Сам не нашёл. Порт видно в настройках эмулятора:"
    Say "  BlueStacks: «Настройки» → «Дополнительно» → «Android Debug Bridge» — строка вида 127.0.0.1:5555."
    Say "  Если ADB там выключен, включите его и нажмите «Сохранить»."
    Say ""
    $manual = Ask 'Введите порт (или адрес вида 127.0.0.1:5555), пусто — пропустить'
    if (-not $manual) { return $false }
    if ($manual -notmatch ':') { $manual = "127.0.0.1:$manual" }
    AdbRaw connect $manual | Out-Null
    return (WaitForDevice 6)
}

function Connect {
    Head "Как подключён телефон"
    Say "  1 — кабелем USB"
    Say "  2 — по Wi-Fi, без проводов (на Windows это надёжнее: не нужны драйверы)"
    Say "  3 — это эмулятор на этом компьютере (BlueStacks, LDPlayer, Nox, MEmu)"
    Say ""
    $choice = Ask 'Введите 1, 2 или 3 и нажмите Enter'

    if ($choice -eq '3') {
        if (-not (ConnectEmulator)) {
            Die @"
Эмулятор так и не отозвался.
Проверьте, что он запущен, а в его настройках включён Android Debug Bridge.
В BlueStacks это «Настройки» → «Дополнительно» → «Android Debug Bridge».
"@
        }
        Say ""
        Say "Эмулятор на связи: $Device"
        return
    }

    if ($choice -eq '2') {
        Say ""
        Say "На телефоне: «Настройки» → «Параметры разработчика» → «Отладка по Wi-Fi» — включите."
        Say "Телефон и компьютер должны быть в одной сети Wi-Fi."
        Say ""
        Say "  1 — подключаюсь с этого компьютера впервые (нужен код сопряжения)"
        Say "  2 — уже подключался раньше"
        Say ""
        $first = Ask 'Введите 1 или 2'
        if ($first -ne '2') {
            Say ""
            Say "На телефоне откройте «Отладка по Wi-Fi» → «Подключение устройства с помощью кода»."
            Say "Появятся адрес вида 192.168.1.5:37105 и код из шести цифр."
            $paddr = Ask 'Адрес из окна с кодом'
            $pcode = Ask 'Код из шести цифр'
            $result = (AdbRaw pair $paddr $pcode) -join ' '
            if ($result -notmatch 'Successfully paired') {
                Die "Сопряжение не прошло. Код живёт около минуты — откройте окно заново и повторите."
            }
        }
        Say ""
        Say "Теперь адрес с главного экрана «Отладка по Wi-Fi» — строка «IP-адрес и порт»."
        Say "Это другой порт, не тот, что был в окне с кодом."
        $caddr = Ask 'Адрес и порт'
        AdbRaw connect $caddr | Out-Null
    } else {
        Say ""
        Say "Подключите телефон кабелем. Кабель должен быть для передачи данных, а не только для зарядки."
        Say "На телефоне разрешите отладку по USB, если появится окно."
    }

    if (-not (WaitForDevice)) {
        Die @"
Телефон так и не появился.
Проверьте: включена ли «Отладка по USB» в параметрах разработчика,
разрешили ли вы отладку на экране телефона,
и — на Samsung — выключен ли «Auto Blocker» в настройках безопасности.
Если по кабелю не выходит, попробуйте способ по Wi-Fi: для него драйверы не нужны.
"@
    }
    Say ""
    Say "Телефон на связи: $Device"
}

function RequireReady {
    if ((Sh 'pm list packages') -notmatch [regex]::Escape("package:$PkgGame")) {
        Die "Игра Invokers: Titan Legacy здесь не найдена."
    }
    if ((Sh "[ -d '$GameDir' ] && echo yes") -notmatch 'yes') {
        Die "Папка с текстами игры не найдена. Запустите игру хотя бы один раз."
    }
    # This used to gate on the shell belonging to the ext_data_rw group. That is the wrong question:
    # the group only exists on the Android versions that introduced scoped storage, so an emulator
    # running an older release was turned away while its shell could write to the directory perfectly
    # well. Ask the directory itself instead — it answers for every Android, and for the corporate
    # policies and Secure Folder clones the group check was actually meant to catch.
    $probe = "$GameDir/.invokersru-probe"
    $ok = Sh "rm -f '$probe' 2>/dev/null; : > '$probe' 2>/dev/null && echo yes; rm -f '$probe' 2>/dev/null"
    if ($ok -notmatch 'yes') {
        Die @"
Нет доступа на запись в папку с текстами игры.

На телефоне так бывает на рабочих и корпоративных устройствах, в защищённой папке
и в клонах приложений. В эмуляторе — если он запущен от другого пользователя.
"@
    }
}

function RequireLanguage {
    if ((Sh "[ -f '$GameDir/$Target' ] && echo yes") -notmatch 'yes') {
        Die "Украинский текст ещё не загружен.`nОткройте игру, в настройках выберите УКРАИНСКИЙ язык, дождитесь загрузки и закройте игру."
    }
    if ((Sh "[ -f '$GameDir/$English' ] && echo yes") -notmatch 'yes') {
        Die "Английский текст игры не найден — без него перевод собрать нельзя. Запустите игру и дождитесь загрузки."
    }
}

# Installs or upgrades the phone-side builder. Checking the version matters: someone who installed
# the first release still has a build with no bridge in it, and it would sit there doing nothing.
function EnsureApp {
    $apk = Get-ChildItem -Path $Here -Filter 'Rusifikator-Invokers-Android-*.apk' -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if (-not $apk) {
        Die "Рядом со скриптом нет файла Rusifikator-Invokers-Android-*.apk. Он должен лежать в той же папке — скачайте архив целиком."
    }
    $apkVersion = ($apk.BaseName -split '-')[-1]

    $installed = ''
    $dump = Sh "dumpsys package $PkgApp"
    if ($dump -match 'versionName=([^\s]+)') { $installed = $Matches[1] }
    if ($installed -eq $apkVersion) { return }

    if ($installed) { Say "Обновляю приложение-сборщик на телефоне ($installed → $apkVersion)…" }
    else { Say "Ставлю приложение-сборщик на телефон…" }

    $out = (& $Adb -s $Device install -r $apk.FullName 2>&1) -join ' '
    if ($out -match 'Success') { return }
    if ($out -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures') {
        Die "На телефоне стоит приложение «Русификатор Invokers» из другого источника.`nУдалите его на телефоне и запустите этот файл заново."
    }
    Die "Не удалось установить приложение на телефон: $out"
}

# The version stamp the game keeps beside its tables. Naming it turns "something did not fit" into
# something the player can act on or report.
function GameStamp { return (Sh "cat '$GameDir/$Target.ver' 2>/dev/null").Trim() }

# The phone reports how many rows it applied. Comparing that with the catalog it was handed is the only
# signal available on this side that the game rewrote its own text: rows whose English changed no
# longer match anything and quietly stay English.
function AppliedNote([string]$applied) {
    $count = 0
    if (-not [int]::TryParse($applied, [ref]$count)) { return 'Перевод установлен.' }
    $catalog = Join-Path $Work 'ru_RU.jsonl'
    if (-not (Test-Path -LiteralPath $catalog)) { return "Переведено строк: $applied." }
    $total = (Get-Content -LiteralPath $catalog -ReadCount 0).Count
    $left = $total - $count
    if ($left -gt 50) {
        return "Переведено строк: $applied из $total.`n`nОстальные $left остались английскими — игра изменила эти тексты после того, как их перевели. Перевод для них появится в следующем обновлении каталога."
    }
    return "Переведено строк: $applied."
}

function RemoteSha([string]$path) {
    $out = Sh "sha256sum '$path' 2>/dev/null"
    if (-not $out) { return '' }
    return ($out.Trim() -split '\s+')[0].ToLower()
}

# The target is deleted first on purpose. Copying over an existing file keeps the game's ownership,
# and ownership is the only thing that tells a pristine file from one a tool wrote: without a fresh
# shell-owned file, a run that has lost its saved original would record a patched file as the
# original and the real Ukrainian text would be gone for good.
function InstallFile([string]$source) {
    Sh "rm -f '$GameDir/$Target' && cp -f '$source' '$GameDir/$Target' && chmod 660 '$GameDir/$Target'" | Out-Null
}

function OwnerOf([string]$path) {
    $out = Sh "ls -l '$path' 2>/dev/null"
    if (-not $out) { return '' }
    return ($out.Trim() -split '\s+')[2]
}

function InitPhonePaths {
    Sh "mkdir -p '$Bridge/backup' '$Bridge/in' '$Bridge/out'" | Out-Null
}

function StateGet([string]$key) {
    $line = Sh "grep -m1 '^$key=' '$PhoneState' 2>/dev/null"
    if (-not $line) { return '' }
    return $line.Trim().Substring($key.Length + 1)
}

function StatePut([string]$original, [string]$patched) {
    # Backslash-n on purpose: printf runs on the phone and expands it there.
    Sh "printf 'original=%s\npatched=%s\n' '$original' '$patched' > '$PhoneState'" | Out-Null
}

# Fetched every run rather than cached forever: translations keep changing, and a stale local copy
# would quietly install last month's text. HttpClient is used instead of Invoke-WebRequest because
# only it asks for and unpacks gzip, which turns 35 MB on the wire into about 6.
function PushCatalog {
    $localFile = Join-Path $Work 'ru_RU.jsonl'
    Say "Скачиваю свежий перевод…"
    try {
        Add-Type -AssemblyName System.Net.Http
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor
                                          [System.Net.DecompressionMethods]::Deflate
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromMinutes(10)
        $bytes = $client.GetByteArrayAsync($CatalogUrl).Result
        if ($bytes.Length -lt 1000000) { throw "слишком маленький ответ" }
        [System.IO.File]::WriteAllBytes("$localFile.part", $bytes)
        Move-Item -Force "$localFile.part" $localFile
    } catch {
        Remove-Item "$localFile.part" -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $localFile)) { Die "Не удалось скачать перевод. Проверьте интернет." }
        Say "Не получилось скачать — ставлю тот перевод, что уже скачан раньше."
    }
    $want = ShaLocal $localFile
    if ($want -ne (RemoteSha "$Bridge/in/ru_RU.jsonl")) {
        Say "Передаю перевод на телефон…"
        AdbDev push $localFile "$Bridge/in/ru_RU.jsonl" | Out-Null
    }
}

function DoApply {
    RequireLanguage
    EnsureApp
    InitPhonePaths

    Head "Установка"
    Say "Закрываю игру…"
    Sh "am force-stop $PkgGame" | Out-Null

    $current = RemoteSha "$GameDir/$Target"
    $owner   = OwnerOf "$GameDir/$Target"
    if (-not $current) { Die "Не удалось прочитать файл игры." }
    $knownOriginal = StateGet 'original'
    $knownPatched  = StateGet 'patched'

    # The game writes its own files as its own user; anything a tool put there is owned by shell.
    # Recording such a file as the original would destroy the real Ukrainian text for good.
    if ($owner -eq 'shell' -and $current -ne $knownPatched -and $current -ne $knownOriginal) {
        Die "Файл текста уже кем-то подменён, а оригинала у меня нет.`nВыберите в главном меню пункт 3 — «вернуть оригинал начисто», потом повторите установку."
    }

    if ($current -eq $knownPatched -and $knownOriginal) {
        $original = $knownOriginal
        if ((RemoteSha $PhoneBackup) -ne $original) {
            Die "Сохранённый оригинал повреждён. Выберите пункт 3 — «вернуть оригинал начисто»."
        }
        Say "Ставлю поверх прошлого перевода, копия оригинала цела."
    } else {
        $original = $current
        if ((RemoteSha $PhoneBackup) -ne $original) {
            Say "Сохраняю оригинал…"
            Sh "cp -f '$GameDir/$Target' '$PhoneBackup'" | Out-Null
            if ((RemoteSha $PhoneBackup) -ne $original) {
                Die "Резервная копия не сошлась, ничего не изменено."
            }
        }
        StatePut $original ''
    }

    PushCatalog

    Say "Передаю файлы игры сборщику…"
    Sh "rm -f '$Bridge/out/$Target' '$Bridge/out/status.txt'" | Out-Null
    Sh "cp -f '$GameDir/$English' '$Bridge/in/$English'" | Out-Null
    # Build from the saved original rather than from whatever is installed, so a repeat run is
    # always a clean rebuild and never a patch on top of a patch.
    Sh "cp -f '$PhoneBackup' '$Bridge/in/$Target'" | Out-Null

    Say "Телефон собирает перевод…"
    Sh "am start -n $PkgApp/.MainActivity -a ru.invokers.patcher.BRIDGE" | Out-Null

    $status = ''
    for ($waited = 0; $waited -lt 300; $waited += 2) {
        Start-Sleep -Seconds 2
        $status = (Sh "cat '$Bridge/out/status.txt' 2>/dev/null").Trim()
        if ($status) { break }
    }
    if (-not $status) {
        Die "Телефон не ответил за пять минут.`nРазблокируйте экран телефона, откройте приложение «Русификатор Invokers» и повторите."
    }
    if (-not $status.StartsWith('OK')) {
        Die @"
Телефон сообщил об ошибке: $($status -replace '^ERR ', '')

Версия игры на телефоне: $(GameStamp)
Если игра недавно обновилась, перевод под новую версию мог быть ещё не готов.
"@
    }
    $applied = $status.Substring(2).Trim()

    Say "Ставлю перевод в игру…"
    $built = RemoteSha "$Bridge/out/$Target"
    InstallFile "$Bridge/out/$Target"

    if ((RemoteSha "$GameDir/$Target") -ne $built) {
        InstallFile $PhoneBackup
        Die "Файл записался неверно, оригинал возвращён."
    }

    StatePut $original $built
    Sh "rm -f '$Bridge/out/$Target' '$Bridge/in/ru_RU.jsonl'" | Out-Null

    Head "Готово"
    Say (AppliedNote $applied)
    Say ""
    Say "ГЛАВНОЕ ПРАВИЛО: не открывайте выбор языка в настройках игры."
    Say "При выборе любого языка игра заново скачает текст и сотрёт перевод."
    Say "Язык должен остаться украинским."
    Say ""
    Say "После обновления игры перевод нужно поставить заново — просто запустите этот файл ещё раз."
}

function DoRestore {
    InitPhonePaths
    $original = StateGet 'original'
    if (-not $original -or (RemoteSha $PhoneBackup) -ne $original) {
        Die "Сохранённого оригинала на телефоне нет. Вернуть оригинал можно пунктом 3 — игра скачает текст сама."
    }
    Head "Возврат оригинала"
    Sh "am force-stop $PkgGame" | Out-Null
    InstallFile $PhoneBackup
    if ((RemoteSha "$GameDir/$Target") -ne $original) { Die "Файл записался неверно." }
    StatePut $original ''
    Say "Оригинальный украинский текст на месте."
}

function DoReset {
    Head "Возврат оригинала начисто"
    Say "Удаляю текст и метку версии, чтобы игра скачала оригинал с сервера."
    # Deleting the file alone is not enough: the stamp still claims the cache is current and the
    # client quietly falls back to the copy inside its own package instead of fetching a fresh one.
    Sh "am force-stop $PkgGame" | Out-Null
    Sh "rm -f '$GameDir/$Target' '$GameDir/$Stamp'" | Out-Null
    Sh "rm -f '$Bridge/backup/state.txt' '$Bridge/backup/original.bin'" | Out-Null
    Say "Запускаю игру. Дождитесь главного экрана и не трогайте настройки языка."
    Sh "monkey -p $PkgGame -c android.intent.category.LAUNCHER 1" | Out-Null

    for ($waited = 0; $waited -lt 240; $waited += 3) {
        Start-Sleep -Seconds 3
        if ((Sh "[ -f '$GameDir/$Target' ] && echo yes") -match 'yes') { break }
    }
    if ((Sh "[ -f '$GameDir/$Target' ] && echo yes") -notmatch 'yes') {
        Die "Игра не скачала текст за четыре минуты. Откройте её вручную и дождитесь главного экрана."
    }
    Say "Оригинал скачан заново. Владелец файла: $(OwnerOf "$GameDir/$Target")"
}

Write-Host ""
Write-Host "  Русификатор Invokers: Titan Legacy — установка на Android"
Write-Host "  Любительский перевод. Проект не связан с HitZone Inc."
Write-Host ""

EnsureAdb

Head "Что делаем"
Say "  1 — установить перевод"
Say "  2 — вернуть оригинал из резервной копии"
Say "  3 — вернуть оригинал начисто (игра скачает текст сама)"
Say "  0 — выйти"
Say ""
$action = Ask 'Введите цифру и нажмите Enter'
if ($action -eq '0') { Finish 0 }

Connect
RequireReady

switch ($action) {
    '1' { DoApply }
    '2' { DoRestore }
    '3' { InitPhonePaths; DoReset }
    default { Die "Такого пункта нет." }
}

Finish 0
