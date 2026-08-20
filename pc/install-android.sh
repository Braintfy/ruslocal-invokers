#!/usr/bin/env bash
# Русификатор Invokers: установка перевода на Android с компьютера.
#
# The computer is only a courier here. It cannot build anything — that needs the game's own two
# language files and the catalog — so instead it copies those files into the patcher app's own
# directory, where adb may write and the app may read, asks the app to compose the Russian file, and
# copies the result back into the game. Everything the computer needs is adb and nothing else, which
# is why this script can bootstrap itself on a machine with no developer tools at all.

set -uo pipefail

PKG_GAME="hitzone.anima.spirit.guardians"
PKG_APP="ru.invokers.patcher"
GAME_DIR="/sdcard/Android/data/${PKG_GAME}/files/i18n"
BRIDGE="/sdcard/Android/data/${PKG_APP}/files/bridge"
TARGET="dl_uk_UA.bin"
ENGLISH="dl_en_US.bin"
STAMP="dl_uk_UA.bin.ver"
CATALOG_URL="https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main/translations/ru_RU.jsonl"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="${HERE}/data"
mkdir -p "$WORK"

# The original and the record of what was installed live on the phone, not next to this script.
# That way a player who re-downloads the archive, or runs it from another computer, can still put
# the original text back instead of being stranded with a patched file and no way home.
PHONE_BACKUP=""
PHONE_STATE=""

ADB=""
DEVICE=""

say() { printf '%s\n' "$*"; }
head1() { printf '\n== %s ==\n\n' "$*"; }
die() { printf '\nНЕ ПОЛУЧИЛОСЬ: %s\n' "$*" >&2; finish 1; }

finish() {
    printf '\nНажмите Enter, чтобы закрыть окно.\n'
    read -r _ || true
    exit "${1:-0}"
}

# The prompt goes to stderr on purpose: the answer is captured through command substitution, and a
# prompt printed on stdout would end up glued to it.
ask() {
    local answer
    printf '%s' "$1" >&2
    read -r answer || answer=""
    printf '%s' "${answer//[[:space:]]/}"
}

sha_local() {
    if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print tolower($1)}'
    else sha256sum "$1" | awk '{print tolower($1)}'; fi
}

adb_() { "$ADB" -s "$DEVICE" "$@" 2>/dev/null | tr -d '\r'; }
adb_raw() { "$ADB" "$@"; }
sh_() { adb_ shell "$@"; }

# grep -q exits at the first match, which hands adb a SIGPIPE and — under pipefail — turns a
# successful check into a failed one. Every device check therefore reads into a variable first.
has_line() {
    case $'\n'"$1"$'\n' in *$'\n'"$2"$'\n'*) return 0 ;; esac
    return 1
}
remote_exists() { [ "$(sh_ "[ -e '$1' ] && echo yes")" = "yes" ]; }

# ---------- adb ----------

ensure_adb() {
    local bundled="${HERE}/platform-tools/adb"
    if [ -x "$bundled" ]; then ADB="$bundled"; return; fi
    if command -v adb >/dev/null 2>&1; then ADB="$(command -v adb)"; return; fi

    say "Утилита связи с телефоном (adb) не найдена — скачиваю её с сайта Google."
    say "Это около 15 МБ, один раз."
    local url zip
    case "$(uname -s)" in
        Darwin) url="https://dl.google.com/android/repository/platform-tools-latest-darwin.zip" ;;
        *)      url="https://dl.google.com/android/repository/platform-tools-latest-linux.zip" ;;
    esac
    zip="${HERE}/platform-tools.zip"
    curl -fL --progress-bar -o "$zip" "$url" || die "Не удалось скачать. Проверьте интернет."
    ( cd "$HERE" && unzip -oq "$zip" ) || die "Не удалось распаковать архив с adb."
    rm -f "$zip"
    [ -x "$bundled" ] || die "Скачали, но adb на месте не оказалось."
    ADB="$bundled"
    say "Готово."
}

wait_for_device() {
    local tries=0
    while :; do
        local list; list="$(adb_raw devices 2>/dev/null | tr -d '\r' | awk 'NR>1 && NF>=2')"
        if [ "${list#*unauthorized}" != "$list" ]; then
            say "Телефон подключён, но не разрешил отладку."
            say "Посмотрите на экран телефона: там окно «Разрешить отладку по USB?»."
            say "Поставьте галочку «Всегда разрешать» и нажмите «Разрешить»."
        fi
        DEVICE="$(printf '%s\n' "$list" | awk '$2=="device"{print $1}' | head -1)"
        [ -n "$DEVICE" ] && return 0
        tries=$((tries + 1))
        [ "$tries" -gt 60 ] && return 1
        sleep 2
        [ $((tries % 5)) -eq 0 ] && say "Жду телефон…"
    done
}

connect() {
    head1 "Как подключён телефон"
    say "  1 — кабелем USB"
    say "  2 — по Wi-Fi, без проводов"
    say ""
    local choice; choice="$(ask 'Введите 1 или 2 и нажмите Enter: ')"

    if [ "$choice" = "2" ]; then
        say ""
        say "На телефоне: «Настройки» → «Параметры разработчика» → «Отладка по Wi-Fi» — включите."
        say "Телефон и компьютер должны быть в одной сети Wi-Fi."
        say ""
        say "  1 — подключаюсь с этого компьютера впервые (нужен код сопряжения)"
        say "  2 — уже подключался раньше"
        say ""
        local first; first="$(ask 'Введите 1 или 2: ')"
        if [ "$first" != "2" ]; then
            say ""
            say "На телефоне откройте «Отладка по Wi-Fi» → «Подключение устройства с помощью кода»."
            say "Появятся адрес вида 192.168.1.5:37105 и код из шести цифр."
            local paddr pcode
            paddr="$(ask 'Адрес из окна с кодом: ')"
            pcode="$(ask 'Код из шести цифр: ')"
            # adb pair reports failure in its output rather than in its exit status.
            local paired; paired="$(adb_raw pair "$paddr" "$pcode" 2>&1)"
            say "$paired"
            case "$paired" in
                *"Successfully paired"*) : ;;
                *) die "Сопряжение не прошло. Код живёт около минуты — откройте на телефоне окно с кодом заново и повторите." ;;
            esac
        fi
        say ""
        say "Теперь адрес с главного экрана «Отладка по Wi-Fi» — строка «IP-адрес и порт»."
        say "Это другой порт, не тот, что был в окне с кодом."
        local caddr; caddr="$(ask 'Адрес и порт: ')"
        adb_raw connect "$caddr" || true
    else
        say ""
        say "Подключите телефон кабелем. Кабель должен быть для передачи данных, а не только для зарядки."
        say "На телефоне разрешите отладку по USB, если появится окно."
    fi

    wait_for_device || die "Телефон так и не появился.
Проверьте: включена ли «Отладка по USB» в параметрах разработчика,
разрешили ли вы отладку на экране телефона,
и — на Samsung — выключен ли «Auto Blocker» в настройках безопасности."
    say ""
    say "Телефон на связи: ${DEVICE}"
}

# ---------- checks ----------

require_ready() {
    local groups; groups="$(sh_ id)"
    case "$groups" in
        *ext_data_rw*) : ;;
        *) die "На этом телефоне adb не имеет доступа к данным приложений. Такое бывает на рабочих
и корпоративных устройствах, а также в защищённой папке и клонах приложений." ;;
    esac

    has_line "$(sh_ 'pm list packages')" "package:${PKG_GAME}" \
        || die "Игра Invokers: Titan Legacy на этом телефоне не найдена."

    remote_exists "$GAME_DIR" \
        || die "Папка с текстами игры не найдена. Запустите игру хотя бы один раз."
}

require_language() {
    remote_exists "${GAME_DIR}/${TARGET}" || die \
"Украинский текст ещё не загружен.
Откройте игру, в настройках выберите УКРАИНСКИЙ язык, дождитесь загрузки и закройте игру."
    remote_exists "${GAME_DIR}/${ENGLISH}" || die \
"Английский текст игры не найден — без него перевод собрать нельзя. Запустите игру и дождитесь загрузки."
}

# Installs or upgrades the phone-side builder. Checking the version matters: someone who installed
# the first release still has a build with no bridge in it, and it would sit there doing nothing.
ensure_app() {
    local apk apk_version installed
    apk="$(ls "${HERE}"/Rusifikator-Invokers-Android-*.apk 2>/dev/null | head -1)"
    [ -n "$apk" ] || die "Рядом со скриптом нет файла Rusifikator-Invokers-Android-*.apk.
Он должен лежать в той же папке — скачайте архив целиком."
    apk_version="$(basename "$apk" .apk)"; apk_version="${apk_version##*-}"
    installed="$(sh_ "dumpsys package ${PKG_APP}" | /usr/bin/sed -n 's/.*versionName=\([^ ]*\).*/\1/p' | head -1)"

    [ "$installed" = "$apk_version" ] && return 0

    if [ -n "$installed" ]; then say "Обновляю приложение-сборщик на телефоне (${installed} → ${apk_version})…"
    else say "Ставлю приложение-сборщик на телефон…"; fi

    local out; out="$(adb_raw -s "$DEVICE" install -r "$apk" 2>&1)"
    case "$out" in
        *Success*) : ;;
        *INSTALL_FAILED_UPDATE_INCOMPATIBLE*|*signatures*)
            die "На телефоне стоит приложение «Русификатор Invokers» из другого источника.
Удалите его на телефоне и запустите этот файл заново." ;;
        *) die "Не удалось установить приложение на телефон: $(printf '%s' "$out" | tail -1)" ;;
    esac
}

remote_sha() { sh_ "sha256sum '$1' 2>/dev/null" | awk '{print tolower($1)}'; }
owner_of() { sh_ "ls -l '$1' 2>/dev/null" | awk '{print $3}'; }

init_phone_paths() {
    PHONE_BACKUP="${BRIDGE}/backup/original.bin"
    PHONE_STATE="${BRIDGE}/backup/state.txt"
    sh_ "mkdir -p '${BRIDGE}/backup' '${BRIDGE}/in' '${BRIDGE}/out'" >/dev/null
}

state_get() {
    local line
    line="$(sh_ "grep -m1 '^$1=' '$PHONE_STATE' 2>/dev/null")"
    printf '%s' "${line#*=}"
}

state_put() {
    sh_ "printf 'original=%s\npatched=%s\n' '$1' '$2' > '$PHONE_STATE'" >/dev/null
}

# ---------- catalog ----------

# The catalog is fetched every run rather than cached forever: translations keep changing, and a
# stale local copy would quietly install last month's text. Compressed it is about 6 MB.
push_catalog() {
    local local_file="${WORK}/ru_RU.jsonl"
    say "Скачиваю свежий перевод…"
    if curl -fL --compressed --progress-bar -o "${local_file}.part" "$CATALOG_URL"; then
        mv -f "${local_file}.part" "$local_file"
    else
        rm -f "${local_file}.part"
        [ -s "$local_file" ] || die "Не удалось скачать перевод. Проверьте интернет."
        say "Не получилось скачать — ставлю тот перевод, что уже скачан раньше."
    fi
    local want have
    want="$(sha_local "$local_file")"
    have="$(remote_sha "${BRIDGE}/in/ru_RU.jsonl")"
    if [ "$want" != "$have" ]; then
        say "Передаю перевод на телефон…"
        adb_raw -s "$DEVICE" push "$local_file" "${BRIDGE}/in/ru_RU.jsonl" >/dev/null 2>&1 \
            || die "Не удалось передать перевод на телефон."
    fi
}

# ---------- actions ----------

do_apply() {
    require_language
    ensure_app
    init_phone_paths

    head1 "Установка"
    say "Закрываю игру…"
    sh_ "am force-stop ${PKG_GAME}" >/dev/null

    local current owner known_original known_patched
    current="$(remote_sha "${GAME_DIR}/${TARGET}")"
    owner="$(owner_of "${GAME_DIR}/${TARGET}")"
    [ -n "$current" ] || die "Не удалось прочитать файл игры."
    known_original="$(state_get original)"
    known_patched="$(state_get patched)"

    # The game writes its own files as its own user; anything a tool put there is owned by shell.
    # Recording such a file as the original would destroy the real Ukrainian text for good.
    if [ "$owner" = "shell" ] && [ "$current" != "$known_patched" ] && [ "$current" != "$known_original" ]; then
        die "Файл текста уже кем-то подменён, а оригинала у меня нет.
Выберите в главном меню пункт 3 — «вернуть оригинал начисто», потом повторите установку."
    fi

    local original
    if [ "$current" = "$known_patched" ] && [ -n "$known_original" ]; then
        original="$known_original"
        [ "$(remote_sha "$PHONE_BACKUP")" = "$original" ] \
            || die "Сохранённый оригинал повреждён. Выберите пункт 3 — «вернуть оригинал начисто»."
        say "Ставлю поверх прошлого перевода, копия оригинала цела."
    else
        original="$current"
        if [ "$(remote_sha "$PHONE_BACKUP")" != "$original" ]; then
            say "Сохраняю оригинал…"
            sh_ "cp -f '${GAME_DIR}/${TARGET}' '$PHONE_BACKUP'" >/dev/null
            [ "$(remote_sha "$PHONE_BACKUP")" = "$original" ] \
                || die "Резервная копия не сошлась, ничего не изменено."
        fi
        state_put "$original" ""
    fi

    push_catalog

    say "Передаю файлы игры сборщику…"
    sh_ "rm -f '${BRIDGE}/out/${TARGET}' '${BRIDGE}/out/status.txt'" >/dev/null
    sh_ "cp -f '${GAME_DIR}/${ENGLISH}' '${BRIDGE}/in/${ENGLISH}'" >/dev/null
    # Build from the saved original rather than from whatever is installed, so a repeat run is
    # always a clean rebuild and never a patch on top of a patch.
    sh_ "cp -f '$PHONE_BACKUP' '${BRIDGE}/in/${TARGET}'" >/dev/null

    say "Телефон собирает перевод…"
    sh_ "am start -n ${PKG_APP}/.MainActivity -a ru.invokers.patcher.BRIDGE" >/dev/null

    local status="" waited=0
    while [ "$waited" -lt 300 ]; do
        sleep 2; waited=$((waited + 2))
        status="$(sh_ "cat '${BRIDGE}/out/status.txt' 2>/dev/null")"
        [ -n "$status" ] && break
    done
    [ -n "$status" ] || die "Телефон не ответил за пять минут.
Разблокируйте экран телефона, откройте приложение «Русификатор Invokers» и повторите."
    case "$status" in
        OK*) : ;;
        *) die "Телефон сообщил об ошибке: ${status#ERR }" ;;
    esac
    local applied="${status#OK }"

    say "Ставлю перевод в игру…"
    local built; built="$(remote_sha "${BRIDGE}/out/${TARGET}")"
    sh_ "cp -f '${BRIDGE}/out/${TARGET}' '${GAME_DIR}/${TARGET}'" >/dev/null
    sh_ "chmod 660 '${GAME_DIR}/${TARGET}'" >/dev/null

    local installed; installed="$(remote_sha "${GAME_DIR}/${TARGET}")"
    if [ "$installed" != "$built" ]; then
        sh_ "cp -f '$PHONE_BACKUP' '${GAME_DIR}/${TARGET}'" >/dev/null
        die "Файл записался неверно, оригинал возвращён."
    fi

    state_put "$original" "$installed"
    sh_ "rm -f '${BRIDGE}/out/${TARGET}' '${BRIDGE}/in/ru_RU.jsonl'" >/dev/null

    head1 "Готово"
    say "Переведено строк: ${applied}"
    say ""
    say "ГЛАВНОЕ ПРАВИЛО: не открывайте выбор языка в настройках игры."
    say "При выборе любого языка игра заново скачает текст и сотрёт перевод."
    say "Язык должен остаться украинским."
    say ""
    say "После обновления игры перевод нужно поставить заново — просто запустите этот файл ещё раз."
}

do_restore() {
    init_phone_paths
    local original; original="$(state_get original)"
    [ -n "$original" ] && [ "$(remote_sha "$PHONE_BACKUP")" = "$original" ] || die \
"Сохранённого оригинала на телефоне нет. Вернуть оригинал можно пунктом 3 — игра скачает текст сама."

    head1 "Возврат оригинала"
    sh_ "am force-stop ${PKG_GAME}" >/dev/null
    sh_ "cp -f '$PHONE_BACKUP' '${GAME_DIR}/${TARGET}'" >/dev/null
    sh_ "chmod 660 '${GAME_DIR}/${TARGET}'" >/dev/null
    [ "$(remote_sha "${GAME_DIR}/${TARGET}")" = "$original" ] || die "Файл записался неверно."
    state_put "$original" ""
    say "Оригинальный украинский текст на месте."
}

do_reset() {
    head1 "Возврат оригинала начисто"
    say "Удаляю текст и метку версии, чтобы игра скачала оригинал с сервера."
    # Deleting the file alone is not enough: the stamp still claims the cache is current and the
    # client quietly falls back to the copy inside its own package instead of fetching a fresh one.
    sh_ "am force-stop ${PKG_GAME}" >/dev/null
    sh_ "rm -f '${GAME_DIR}/${TARGET}' '${GAME_DIR}/${STAMP}'" >/dev/null
    sh_ "rm -f '${BRIDGE}/backup/state.txt' '${BRIDGE}/backup/original.bin'" >/dev/null
    say "Запускаю игру. Дождитесь главного экрана и не трогайте настройки языка."
    sh_ "monkey -p ${PKG_GAME} -c android.intent.category.LAUNCHER 1" >/dev/null

    local waited=0
    while [ "$waited" -lt 240 ]; do
        sleep 3; waited=$((waited + 3))
        remote_exists "${GAME_DIR}/${TARGET}" && break
    done
    remote_exists "${GAME_DIR}/${TARGET}" \
        || die "Игра не скачала текст за четыре минуты. Откройте её вручную и дождитесь главного экрана."
    say "Оригинал скачан заново. Владелец файла: $(owner_of "${GAME_DIR}/${TARGET}")"
}

# ---------- main ----------

printf '\n'
printf '  Русификатор Invokers: Titan Legacy — установка на Android\n'
printf '  Любительский перевод. Проект не связан с HitZone Inc.\n'
printf '\n'

ensure_adb

head1 "Что делаем"
say "  1 — установить перевод"
say "  2 — вернуть оригинал из резервной копии"
say "  3 — вернуть оригинал начисто (игра скачает текст сама)"
say "  0 — выйти"
say ""
ACTION="$(ask 'Введите цифру и нажмите Enter: ')"
[ "$ACTION" = "0" ] && finish 0

connect
require_ready

case "$ACTION" in
    1) do_apply ;;
    2) do_restore ;;
    3) init_phone_paths; do_reset ;;
    *) die "Такого пункта нет." ;;
esac

finish 0
