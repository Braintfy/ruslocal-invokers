#!/usr/bin/env bash
# Installs the Russian translation onto an Android device or emulator over ADB.
#
# An app running on the phone itself cannot do this: since Android 11 another package's
# Android/data is closed to both the Storage Access Framework and MANAGE_EXTERNAL_STORAGE, and the
# internal data directory is closed by UID isolation. The adb shell user is the exception — the
# daemon grants it ext_data_rw specifically so it can reach Android/data.
#
# The Russian file is composed from the files already on the device, so it adapts to whatever
# content version that device happens to have instead of assuming one.
#
# Usage:
#   android-patch.sh status  [--device SERIAL]
#   android-patch.sh apply   [--device SERIAL] [--translations FILE]
#   android-patch.sh restore [--device SERIAL]
#   android-patch.sh reset   [--device SERIAL]   — вернуть оригинал скачиванием с сервера

set -uo pipefail

PKG="hitzone.anima.spirit.guardians"
TARGET_NAME="dl_uk_UA.bin"
ENGLISH_NAME="dl_en_US.bin"
STAMP_NAME="dl_uk_UA.bin.ver"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STATE_DIR="${HOME}/Library/Application Support/InvokersRu/android"
[ "$(uname -s)" = "Darwin" ] || STATE_DIR="${HOME}/.local/share/InvokersRu/android"
BACKUP_DIR="${STATE_DIR}/backups"
WORK_DIR="${STATE_DIR}/work"

die() { printf 'ОШИБКА: %s\n' "$*" >&2; exit 1; }
note() { printf '%s\n' "$*"; }
sha256_of() { shasum -a 256 "$1" | awk '{print toupper($1)}'; }

DEVICE=""
TRANSLATIONS="${REPO_ROOT}/translations/ru_RU.jsonl"
ACTION="${1:-}"
[ -n "$ACTION" ] || die "Использование: $(basename "$0") status|apply|restore|reset [--device SERIAL]"
shift || true
while [ $# -gt 0 ]; do
    case "$1" in
        --device) DEVICE="${2:-}"; [ -n "$DEVICE" ] || die "--device требует значение."; shift 2 ;;
        --translations) TRANSLATIONS="${2:-}"; [ -n "$TRANSLATIONS" ] || die "--translations требует значение."; shift 2 ;;
        *) die "Неизвестный параметр: $1" ;;
    esac
done

command -v adb >/dev/null 2>&1 || die "adb не найден. Установите: brew install --cask android-platform-tools"

adb_() { if [ -n "$DEVICE" ]; then adb -s "$DEVICE" "$@"; else adb "$@"; fi; }

# ---------- device selection ----------

select_device() {
    local list count
    list="$(adb devices | awk 'NR>1 && NF>=2 {print $1"\t"$2}')"
    [ -n "$list" ] || die "Устройство не найдено. Подключите телефон кабелем и включите отладку по USB."

    if printf '%s\n' "$list" | grep -q "unauthorized"; then
        die "Устройство подключено, но не авторизовано. На экране телефона нажмите «Разрешить отладку по USB»."
    fi

    count="$(printf '%s\n' "$list" | grep -c "device$")"
    [ "$count" -gt 0 ] || die "Устройство есть, но не в рабочем состоянии: $(printf '%s' "$list" | tr '\t' ' ')"
    if [ -z "$DEVICE" ] && [ "$count" -gt 1 ]; then
        note "Подключено несколько устройств:"
        printf '%s\n' "$list" | grep "device$" | awk '{print "  " $1}'
        die "Укажите нужное через --device SERIAL."
    fi
    [ -n "$DEVICE" ] || DEVICE="$(printf '%s\n' "$list" | grep "device$" | head -1 | cut -f1)"
}

# The whole approach rests on the shell user holding ext_data_rw; without it nothing below works.
require_shell_access() {
    local groups
    groups="$(adb_ shell id 2>/dev/null | tr -d '\r')"
    case "$groups" in
        *ext_data_rw*) : ;;
        *) die "У adb нет доступа к данным приложений на этом устройстве (нет группы ext_data_rw). Такое бывает на некоторых прошивках и на управляемых устройствах." ;;
    esac
}

require_game() {
    adb_ shell pm list packages 2>/dev/null | tr -d '\r' | grep -qx "package:${PKG}" \
        || die "Игра Invokers: Titan Legacy не установлена на этом устройстве."
}

DIR=""
resolve_dir() {
    DIR="/sdcard/Android/data/${PKG}/files/i18n"
    adb_ shell "[ -d '$DIR' ]" 2>/dev/null \
        || die "Каталог локализации не найден. Запустите игру хотя бы один раз.
Если игра стоит в защищённой папке, рабочем профиле или клоне приложения, этот способ не сработает: adb туда не попадёт."
}

game_version() { adb_ shell dumpsys package "$PKG" 2>/dev/null | tr -d '\r' | sed -n 's/.*versionName=\([^ ]*\).*/\1/p' | head -1; }
# The game writes its own files as its app user, while anything pushed over adb lands owned by
# shell. That difference is what tells a pristine file apart from one some tool already replaced,
# so a patched file never gets recorded as the original.
target_owner() { adb_ shell "ls -l '${DIR}/${TARGET_NAME}'" 2>/dev/null | tr -d '\r' | awk '{print $3}'; }
remote_sha() { adb_ shell "sha256sum '$1'" 2>/dev/null | tr -d '\r' | awk '{print toupper($1)}'; }
game_running() { [ -n "$(adb_ shell pidof "$PKG" 2>/dev/null | tr -d '\r')" ]; }

state_file() { printf '%s/%s.json' "$STATE_DIR" "$DEVICE"; }
state_get() {
    local f; f="$(state_file)"
    [ -f "$f" ] || return 1
    /usr/bin/sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$f" | head -1
}

# Fills CLI_CMD with a working invocation. Candidates are probed by actually running them: the
# app-host produced by a plain build looks executable but refuses to start unless the .NET runtime is
# on PATH, while the copy inside the macOS app is self-contained and always works.
CLI_CMD=()
find_cli() {
    local dotnet_bin=""
    command -v dotnet >/dev/null 2>&1 && dotnet_bin="$(command -v dotnet)"
    [ -n "$dotnet_bin" ] || [ ! -x "${HOME}/.dotnet/dotnet" ] || dotnet_bin="${HOME}/.dotnet/dotnet"

    local self_contained=(
        "${INVOKERSRU_CLI:-}"
        "/Applications/Русификатор Invokers.app/Contents/Resources/InvokersRu.Cli"
        "${REPO_ROOT}/work/mac-app/Русификатор Invokers.app/Contents/Resources/InvokersRu.Cli"
        "${REPO_ROOT}/src/InvokersRu.Cli/bin/Release/net10.0/InvokersRu.Cli"
    )
    local candidate
    for candidate in "${self_contained[@]}"; do
        [ -n "$candidate" ] && [ -x "$candidate" ] || continue
        if "$candidate" >/dev/null 2>&1; then CLI_CMD=("$candidate"); return 0; fi
    done

    local dll="${REPO_ROOT}/src/InvokersRu.Cli/bin/Release/net10.0/InvokersRu.Cli.dll"
    if [ -f "$dll" ] && [ -n "$dotnet_bin" ] && "$dotnet_bin" "$dll" >/dev/null 2>&1; then
        CLI_CMD=("$dotnet_bin" "$dll"); return 0
    fi
    return 1
}

mkdir -p "$STATE_DIR" "$BACKUP_DIR" "$WORK_DIR"
select_device
require_shell_access
require_game
resolve_dir

case "$ACTION" in
status)
    note "Устройство:   ${DEVICE}"
    note "Версия игры:  $(game_version)"
    note "Каталог:      ${DIR}"
    current="$(remote_sha "${DIR}/${TARGET_NAME}")"
    [ -n "$current" ] || die "Украинский языковой файл ещё не загружен. В игре выберите украинский язык, дождитесь загрузки и закройте игру."
    note "Текущий SHA:  ${current}"
    note "Версия файла: $(adb_ shell "cat '${DIR}/${STAMP_NAME}'" 2>/dev/null | tr -d '\r')"
    if orig="$(state_get original_sha256)"; then
        patched="$(state_get patched_sha256)"
        note "Оригинал:     ${orig}"
        if [ "$current" = "$patched" ]; then note "Состояние:    установлен русский перевод"
        elif [ "$current" = "$orig" ]; then note "Состояние:    восстановлен оригинал"
        else note "Состояние:    файл изменился сам — игра перекачала его после смены языка или обновления"
        fi
    else
        note "Состояние:    перевод ещё не устанавливался"
    fi
    ;;

apply)
    [ -f "$TRANSLATIONS" ] || die "Файл перевода не найден: ${TRANSLATIONS}"
    find_cli || die "Не найден работающий InvokersRu.Cli. Соберите его: dotnet build src/InvokersRu.Cli/InvokersRu.Cli.csproj -c Release"

    current="$(remote_sha "${DIR}/${TARGET_NAME}")"
    [ -n "$current" ] || die "Украинский языковой файл ещё не загружен. В игре выберите украинский язык, дождитесь загрузки и закройте игру."

    note "Устройство: ${DEVICE}, игра $(game_version)"
    note "Закрываю игру…"
    adb_ shell am force-stop "$PKG" >/dev/null 2>&1
    game_running && die "Не удалось закрыть игру. Закройте её вручную и повторите."

    # Compose from the device's own files so the build matches that device's content version.
    note "Читаю файлы с устройства…"
    rm -f "${WORK_DIR}/${ENGLISH_NAME}" "${WORK_DIR}/${TARGET_NAME}"
    adb_ pull "${DIR}/${ENGLISH_NAME}" "${WORK_DIR}/${ENGLISH_NAME}" >/dev/null 2>&1 || die "Не удалось прочитать ${ENGLISH_NAME}."
    adb_ pull "${DIR}/${TARGET_NAME}" "${WORK_DIR}/${TARGET_NAME}" >/dev/null 2>&1 || die "Не удалось прочитать ${TARGET_NAME}."

    # Refuse to record someone else's replacement as the original, otherwise restore would put a
    # patched file back and the real Ukrainian text would be lost for good.
    if [ "$(target_owner)" = "shell" ] \
       && [ "$current" != "$(state_get patched_sha256 || true)" ] \
       && [ "$current" != "$(state_get original_sha256 || true)" ]; then
        die "Файл локализации уже подменён каким-то инструментом, и оригинала у меня нет.
Верните оригинал командой «$(basename "$0") reset», затем повторите установку."
    fi

    # Whatever is on the device right now is the original unless we ourselves put the current file there.
    original="$current"
    if [ "$current" = "$(state_get patched_sha256 || true)" ]; then
        original="$(state_get original_sha256)"
        backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
        [ -f "$backup" ] && [ "$(sha256_of "$backup")" = "$original" ] \
            || die "Прошлая резервная копия повреждена. Переключите язык в игре, чтобы клиент скачал оригинал заново."
        note "Переустановка поверх прошлого перевода, прежняя копия оригинала сохранена."
        cp -f "$backup" "${WORK_DIR}/${TARGET_NAME}"
    else
        backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
        if [ ! -f "$backup" ] || [ "$(sha256_of "$backup")" != "$original" ]; then
            cp -f "${WORK_DIR}/${TARGET_NAME}" "${backup}.tmp"
            [ "$(sha256_of "${backup}.tmp")" = "$original" ] || { rm -f "${backup}.tmp"; die "Резервная копия не сошлась по контрольной сумме, ничего не изменено."; }
            mv -f "${backup}.tmp" "$backup"
        fi
        note "Оригинал сохранён: ${backup}"
    fi

    note "Собираю перевод…"
    built="${WORK_DIR}/${TARGET_NAME}.ru"
    rm -f "$built" "${WORK_DIR}/report.json"
    if ! "${CLI_CMD[@]}" build --english "${WORK_DIR}/${ENGLISH_NAME}" --base "${WORK_DIR}/${TARGET_NAME}" \
        --translations "$TRANSLATIONS" --output "$built" --report "${WORK_DIR}/report.json" \
        --include-draft --raw --per-locale-content-version >"${WORK_DIR}/build.log" 2>&1; then
        note "$(tail -3 "${WORK_DIR}/build.log")"
        die "Не удалось собрать перевод для этой версии игры. Скорее всего игра обновилась и перевод ещё не адаптирован."
    fi
    applied="$(/usr/bin/sed -n 's/.*"applied_ru"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "${WORK_DIR}/report.json" | head -1)"

    note "Устанавливаю…"
    adb_ push "$built" "${DIR}/${TARGET_NAME}" >/dev/null 2>&1 || die "Не удалось записать файл на устройство."

    installed="$(remote_sha "${DIR}/${TARGET_NAME}")"
    expected="$(sha256_of "$built")"
    if [ "$installed" != "$expected" ]; then
        adb_ push "$backup" "${DIR}/${TARGET_NAME}" >/dev/null 2>&1
        die "Установленный файл не сошёлся по контрольной сумме, оригинал возвращён."
    fi

    cat > "$(state_file)" <<JSON
{
  "schema": 1,
  "device": "${DEVICE}",
  "package": "${PKG}",
  "original_sha256": "${original}",
  "patched_sha256": "${installed}",
  "backup_path": "${backup}"
}
JSON

    note ""
    note "Готово. Переведено строк: ${applied:-?}"
    note ""
    note "ВАЖНО: не открывайте выбор языка в настройках игры — клиент заново скачает файл и сотрёт перевод."
    note "Язык должен остаться украинским. После обновления игры перевод нужно установить заново."
    note "Вернуть оригинал: $(basename "$0") restore"
    ;;

restore)
    orig="$(state_get original_sha256)" || die "Перевод на это устройство не устанавливался."
    backup="$(state_get backup_path)"
    [ -f "$backup" ] && [ "$(sha256_of "$backup")" = "$orig" ] || die "Резервная копия повреждена или отсутствует: ${backup}"

    current="$(remote_sha "${DIR}/${TARGET_NAME}")"
    if [ "$current" = "$orig" ]; then note "На устройстве уже оригинальный файл."; exit 0; fi

    adb_ shell am force-stop "$PKG" >/dev/null 2>&1
    adb_ push "$backup" "${DIR}/${TARGET_NAME}" >/dev/null 2>&1 || die "Не удалось восстановить оригинал."
    [ "$(remote_sha "${DIR}/${TARGET_NAME}")" = "$orig" ] || die "Восстановленный файл не сошёлся по контрольной сумме."
    note "Оригинальный украинский текст восстановлен."
    ;;

reset)
    # Deleting the file alone is not enough: the version stamp still claims the cache is current, so
    # the client just falls back to the copy bundled in the APK. Removing the stamp too makes it
    # fetch a pristine file from the server, which is the reliable way back to the original.
    note "Возвращаю оригинал: удаляю файл локализации и метку версии, чтобы игра скачала его заново."
    adb_ shell am force-stop "$PKG" >/dev/null 2>&1
    adb_ shell rm -f "${DIR}/${TARGET_NAME}" "${DIR}/${STAMP_NAME}" >/dev/null 2>&1
    rm -f "$(state_file)"
    note "Запускаю игру, дождитесь загрузки…"
    adb_ shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1

    waited=0
    until adb_ shell "[ -f '${DIR}/${TARGET_NAME}' ]" 2>/dev/null; do
        sleep 3; waited=$((waited + 3))
        [ "$waited" -lt 180 ] || die "Игра не скачала файл за три минуты. Откройте её вручную и дождитесь главного экрана."
    done
    note "Оригинал скачан заново: $(remote_sha "${DIR}/${TARGET_NAME}")"
    note "Владелец: $(target_owner) (файл записан самой игрой — это и есть признак чистого оригинала)"
    ;;

*)
    die "Неизвестное действие: ${ACTION}. Доступны: status, apply, restore, reset."
    ;;
esac
