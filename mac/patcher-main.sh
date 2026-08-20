#!/bin/bash
# Driver for InvokersRu Patcher.app — every message reaches the user through a native dialog,
# because a bundle launched from Finder has nowhere to print to.

set -uo pipefail

# Version of this script. It updates itself from the repository; the bundle around it stays frozen.
APP_VERSION="2.4.0"
# Version of the application bundle, which only changes when the launcher or the CLI has to change.
BUNDLE_VERSION="2.2.0"

REPO_RAW="https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main"
OVERLAY_URL="${REPO_RAW}/translations/ru_RU.jsonl"
MANIFEST_URL="${REPO_RAW}/config/mac-patcher.json"
PATCHER_URL="${REPO_RAW}/mac/patcher-main.sh"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Set by the launcher, because this script may be running from the updatable copy outside the bundle.
RESOURCES="${INVOKERSRU_RESOURCES:-$HERE}"
CLI="${RESOURCES}/InvokersRu.Cli"

SUPPORT_DIR="${HOME}/Library/Application Support/InvokersRu"
WORK_DIR="${SUPPORT_DIR}/work"
BACKUP_DIR="${SUPPORT_DIR}/backups"
RUNTIME_DIR="${SUPPORT_DIR}/runtime"
STATE_FILE="${SUPPORT_DIR}/state.json"
OVERLAY_CACHE="${SUPPORT_DIR}/ru_RU.jsonl"
LOG_FILE="${SUPPORT_DIR}/patcher.log"
RESUME_MARKER="${SUPPORT_DIR}/.resuming"

TARGET_NAME="dl_uk_UA.bin"
ENGLISH_NAME="dl_en_US.bin"
TITLE="Русификатор Invokers"

mkdir -p "$SUPPORT_DIR" "$WORK_DIR" "$BACKUP_DIR" "$RUNTIME_DIR"
exec 2>>"$LOG_FILE"
printf '\n===== %s | v%s =====\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$APP_VERSION" >>"$LOG_FILE"

# ---------- native dialogs ----------

esc() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

say_error() {
    osascript -e "display dialog \"$(esc "$1")\" with title \"$(esc "$TITLE")\" buttons {\"Закрыть\"} default button 1 with icon stop" >/dev/null 2>&1
}

say_info() {
    osascript -e "display dialog \"$(esc "$1")\" with title \"$(esc "$TITLE")\" buttons {\"OK\"} default button 1 with icon note" >/dev/null 2>&1
}

# ask "<text>" "<btn1>" "<btn2>" ... — echoes the pressed button, empty if cancelled.
ask() {
    local text="$1"; shift
    local list="" button
    for button in "$@"; do
        [ -n "$list" ] && list="${list}, "
        list="${list}\"$(esc "$button")\""
    done
    osascript -e "button returned of (display dialog \"$(esc "$text")\" with title \"$(esc "$TITLE")\" buttons {${list}} default button ${#} with icon caution)" 2>/dev/null
}

progress_start() { printf '%s\n' "$1" >>"$LOG_FILE"; }

die() { printf 'FATAL: %s\n' "$1" >>"$LOG_FILE"; say_error "$1"; exit 1; }

sha256_of() { shasum -a 256 "$1" 2>/dev/null | awk '{print toupper($1)}'; }

json_field() {
    [ -f "$1" ] || return 1
    /usr/bin/sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$1" | head -1
}

# ---------- environment ----------

find_cache_root() {
    local containers="${HOME}/Library/Containers" found=() candidate
    [ -d "$containers" ] || return 1
    for container in "$containers"/*/; do
        candidate="${container}Data/Documents/i18n"
        [ -f "${candidate}/dl_en_US.bin" ] && found+=("$candidate")
    done
    [ "${#found[@]}" -eq 1 ] || return 1
    printf '%s\n' "${found[0]}"
}

game_running() { pgrep -f "Invokers.app/Invokers" >/dev/null 2>&1; }

# macOS blocks one app from reading another app's container until the user grants Full Disk Access.
# An app launched from Finder therefore sees "Operation not permitted" where a terminal would not.
can_read_container() { [ -r "$1" ] && head -c 1 "$1" >/dev/null 2>&1; }

open_full_disk_settings() {
    open "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles" >/dev/null 2>&1 \
        || open "/System/Library/PreferencePanes/Security.prefPane" >/dev/null 2>&1 || true
}

relaunch_self() {
    local bundle
    # Resolved from the bundle's Resources, because this script may live outside the bundle.
    bundle="$(cd "${RESOURCES}/../.." 2>/dev/null && pwd)"
    date +%s > "$RESUME_MARKER"
    if [ -n "$bundle" ] && [ -d "$bundle" ]; then
        open -n "$bundle" >/dev/null 2>&1 &
    fi
    exit 0
}

# Blocks on a dialog that a background watcher dismisses the moment the grant appears, so the user
# flips the switch in System Settings and the install simply carries on by itself.
wait_for_disk_access() {
    local probe="$1" dialog_pid watcher_pid
    osascript -e "display dialog \"Ожидание доступа…

Включите переключатель напротив «Русификатор Invokers» в открывшемся окне «Полный доступ к диску».

Как только включите, установка продолжится сама — это окно закроется автоматически. Ничего перезапускать не нужно.\" with title \"$(esc "$TITLE")\" buttons {\"Отмена\"} default button 1 with icon caution giving up after 180" >/dev/null 2>&1 &
    dialog_pid=$!
    (
        while ! can_read_container "$probe"; do sleep 1; done
        kill "$dialog_pid" 2>/dev/null
    ) >/dev/null 2>&1 &
    watcher_pid=$!
    wait "$dialog_pid" 2>/dev/null
    kill "$watcher_pid" 2>/dev/null
    can_read_container "$probe"
}

require_disk_access() {
    local probe="$1" answer
    can_read_container "$probe" && return 0

    answer="$(ask "Нужен доступ к данным игры.

macOS не разрешает приложениям читать файлы других программ, пока вы явно это не позволите. Это стандартное требование системы.

Нажмите «Открыть настройки», включите переключатель напротив «Русификатор Invokers» — и установка продолжится сама, возвращаться сюда не придётся." "Выход" "Открыть настройки")"
    [ "$answer" = "Открыть настройки" ] || exit 0

    open_full_disk_settings
    wait_for_disk_access "$probe" && return 0

    # A grant is bound to the exact application it was created for. After the app is replaced by a new
    # version the old entry keeps showing an enabled switch while granting nothing, and only removing
    # and re-adding it rebuilds the association.
    answer="$(ask "Доступа всё ещё нет.

ЕСЛИ ПЕРЕКЛЮЧАТЕЛЬ УЖЕ ВКЛЮЧЁН — разрешение устарело. Так бывает после обновления русификатора: система помнит старую версию, галка горит, а доступа не даёт.

Что сделать:
1. В окне «Полный доступ к диску» выделите «Русификатор Invokers» и нажмите «−», чтобы удалить строку.
2. Нажмите «+», откройте папку «Программы» и выберите «Русификатор Invokers» заново.
3. Убедитесь, что переключатель включён.

Затем нажмите «Перезапустить» — русификатор откроется и сразу продолжит с этого места." "Выход" "Перезапустить")"
    [ "$answer" = "Перезапустить" ] && relaunch_self
    exit 0
}

atomic_install() {
    local source="$1" target="$2" directory temp
    directory="$(dirname "$target")"
    temp="$(mktemp "${directory}/.${TARGET_NAME}.invokersru.XXXXXX")" || return 1
    if ! cat "$source" > "$temp"; then rm -f "$temp"; return 1; fi
    sync
    if [ "$(sha256_of "$temp")" != "$(sha256_of "$source")" ]; then rm -f "$temp"; return 1; fi
    chmod 644 "$temp"
    mv -f "$temp" "$target"
}

# ---------- update check ----------

# The overlay is tens of megabytes of JSONL, which compresses roughly tenfold in transit.
fetch() { curl -fsSL --compressed --max-time 300 "$1" -o "$2" 2>>"$LOG_FILE"; }

# Replaces this script from the repository without touching the application bundle, so the Full Disk
# Access grant — which macOS pins to the bundle's code signature — keeps working. The download is
# accepted only when its SHA-256 matches the value published in the manifest.
self_update() {
    [ -z "${INVOKERSRU_UPDATED:-}" ] || return 0
    [ ! -f "${SUPPORT_DIR}/no-self-update" ] || return 0

    local manifest="${WORK_DIR}/manifest.json" published expected fresh
    fetch "$MANIFEST_URL" "$manifest" || return 0
    published="$(json_field "$manifest" patcher_version || true)"
    expected="$(json_field "$manifest" patcher_sha256 || true)"
    [ -n "$published" ] && [ -n "$expected" ] || return 0
    [ "$published" != "$APP_VERSION" ] || return 0

    fresh="${WORK_DIR}/patcher.sh.new"
    fetch "$PATCHER_URL" "$fresh" || { rm -f "$fresh"; return 0; }
    if [ "$(sha256_of "$fresh")" != "$(printf '%s' "$expected" | tr '[:lower:]' '[:upper:]')" ]; then
        printf 'self-update refused: checksum mismatch\n' >>"$LOG_FILE"
        rm -f "$fresh"
        return 0
    fi
    if ! /bin/bash -n "$fresh" 2>>"$LOG_FILE"; then
        printf 'self-update refused: syntax check failed\n' >>"$LOG_FILE"
        rm -f "$fresh"
        return 0
    fi

    mkdir -p "$RUNTIME_DIR"
    chmod 755 "$fresh"
    mv -f "$fresh" "${RUNTIME_DIR}/patcher.sh"
    printf 'self-update applied: %s -> %s\n' "$APP_VERSION" "$published" >>"$LOG_FILE"
    say_info "Русификатор обновлён: ${APP_VERSION} → ${published}.

$(json_field "$manifest" notes || true)

Переустанавливать приложение и заново выдавать доступ к диску не нужно."
    INVOKERSRU_UPDATED=1 exec /bin/bash "${RUNTIME_DIR}/patcher.sh" "$@"
}

check_app_update() {
    local manifest="${WORK_DIR}/manifest.json" required notes
    [ -f "$manifest" ] || fetch "$MANIFEST_URL" "$manifest" || return 0
    required="$(json_field "$manifest" minimum_bundle_version || true)"
    [ -n "$required" ] || return 0
    if [ "$required" != "$BUNDLE_VERSION" ]; then
        notes="$(json_field "$manifest" bundle_notes || true)"
        say_info "Вышла новая сборка приложения: ${required} (у вас ${BUNDLE_VERSION}).

${notes}

Обычные обновления ставятся сами, но эту версию нужно скачать заново со страницы проекта на GitHub.

После замены приложения система попросит выдать доступ к диску заново — она привязывает его к конкретной сборке. В списке «Полный доступ к диску» выделите «Русификатор Invokers», нажмите «−», затем «+» и выберите приложение в папке «Программы»."
    fi
}

# Downloads the public overlay. Returns 0 if a usable overlay is at $OVERLAY_CACHE.
refresh_overlay() {
    local fresh="${WORK_DIR}/ru_RU.jsonl.new" fresh_lines cached_lines
    if fetch "$OVERLAY_URL" "$fresh" && [ -s "$fresh" ]; then
        if [ ! -f "$OVERLAY_CACHE" ]; then
            mv -f "$fresh" "$OVERLAY_CACHE"
            printf 'overlay downloaded\n' >>"$LOG_FILE"
            return 0
        fi
        if [ "$(sha256_of "$fresh")" = "$(sha256_of "$OVERLAY_CACHE")" ]; then
            rm -f "$fresh"
            return 0
        fi
        # A catalog that suddenly lost most of its records means something is wrong upstream, not that
        # the translation shrank on purpose. Keeping the copy already on disk avoids turning a good
        # installation back into a mostly-English one without the user asking for it.
        fresh_lines="$(wc -l < "$fresh" | tr -d ' ')"
        cached_lines="$(wc -l < "$OVERLAY_CACHE" | tr -d ' ')"
        if [ "$fresh_lines" -lt $((cached_lines / 2)) ]; then
            printf 'refusing overlay downgrade: fresh=%s cached=%s\n' "$fresh_lines" "$cached_lines" >>"$LOG_FILE"
            rm -f "$fresh"
            return 0
        fi
        mv -f "$fresh" "$OVERLAY_CACHE"
        printf 'overlay updated: %s records\n' "$fresh_lines" >>"$LOG_FILE"
        return 0
    fi
    rm -f "$fresh"
    [ -s "$OVERLAY_CACHE" ]
}

# ---------- Android ----------
#
# An app on the phone itself cannot do this: since Android 11 another package's Android/data is
# closed to the Storage Access Framework and to MANAGE_EXTERNAL_STORAGE alike, and the internal data
# directory is closed by UID isolation. The adb shell user is the documented exception, so the phone
# is patched from here over a cable.

ANDROID_PKG="hitzone.anima.spirit.guardians"
ANDROID_DIR="/sdcard/Android/data/${ANDROID_PKG}/files/i18n"
ANDROID_STATE="${SUPPORT_DIR}/android-state.json"
ADB=""

find_adb() {
    local candidate
    for candidate in "$(command -v adb 2>/dev/null)" \
                     "/usr/local/bin/adb" "/opt/homebrew/bin/adb" \
                     "${HOME}/Library/Android/sdk/platform-tools/adb"; do
        [ -n "$candidate" ] && [ -x "$candidate" ] && { ADB="$candidate"; return 0; }
    done
    return 1
}

# Echoes the serial of a single usable device, or nothing.
android_device() {
    [ -n "$ADB" ] || return 1
    "$ADB" devices 2>/dev/null | awk 'NR>1 && $2=="device" {print $1}' | head -1
}

android_has_game() {
    "$ADB" -s "$1" shell pm list packages 2>/dev/null | tr -d '\r' | grep -qx "package:${ANDROID_PKG}"
}

android_sha() { "$ADB" -s "$1" shell "sha256sum '$2'" 2>/dev/null | tr -d '\r' | awk '{print toupper($1)}'; }
android_owner() { "$ADB" -s "$1" shell "ls -l '${ANDROID_DIR}/${TARGET_NAME}'" 2>/dev/null | tr -d '\r' | awk '{print $3}'; }

android_install() {
    local serial="$1" current original backup built applied

    if ! "$ADB" -s "$serial" shell id 2>/dev/null | tr -d '\r' | grep -q ext_data_rw; then
        say_error "На этом устройстве у ADB нет доступа к данным приложений.

Такое бывает на некоторых прошивках и на устройствах под управлением организации. Установить перевод на этот телефон не получится."
        return 1
    fi
    if ! android_has_game "$serial"; then
        say_error "На подключённом устройстве не установлена игра Invokers: Titan Legacy."
        return 1
    fi

    current="$(android_sha "$serial" "${ANDROID_DIR}/${TARGET_NAME}")"
    if [ -z "$current" ]; then
        say_error "На телефоне ещё нет украинского языкового файла.

Откройте игру, выберите в настройках украинский язык, дождитесь загрузки и полностью закройте игру. Затем повторите."
        return 1
    fi

    progress_start "android: downloading overlay"
    refresh_overlay || { say_error "Не удалось загрузить перевод и нет сохранённой копии."; return 1; }

    # Anything pushed over adb lands owned by shell, while the game writes as its own user. That tells
    # a pristine file apart from one another tool already replaced, so a patched file is never
    # recorded as the original and restore can never put a patched file back.
    local known_patched=""; local known_original=""
    [ -f "$ANDROID_STATE" ] && known_patched="$(json_field "$ANDROID_STATE" patched_sha256 || true)"
    [ -f "$ANDROID_STATE" ] && known_original="$(json_field "$ANDROID_STATE" original_sha256 || true)"
    if [ "$(android_owner "$serial")" = "shell" ] \
       && [ "$current" != "$known_patched" ] && [ "$current" != "$known_original" ]; then
        say_error "Файл локализации на телефоне уже подменён каким-то инструментом, и оригинала нет.

Чтобы вернуть оригинал: в игре переключите язык на другой и обратно на украинский — клиент скачает файл заново."
        return 1
    fi

    "$ADB" -s "$serial" shell am force-stop "$ANDROID_PKG" >/dev/null 2>&1
    mkdir -p "${WORK_DIR}/android"
    rm -f "${WORK_DIR}/android/${ENGLISH_NAME}" "${WORK_DIR}/android/${TARGET_NAME}"
    "$ADB" -s "$serial" pull "${ANDROID_DIR}/${ENGLISH_NAME}" "${WORK_DIR}/android/${ENGLISH_NAME}" >/dev/null 2>&1 || {
        say_error "Не удалось прочитать файлы игры с телефона."; return 1; }
    "$ADB" -s "$serial" pull "${ANDROID_DIR}/${TARGET_NAME}" "${WORK_DIR}/android/${TARGET_NAME}" >/dev/null 2>&1 || {
        say_error "Не удалось прочитать файлы игры с телефона."; return 1; }

    if [ "$current" = "$known_patched" ] && [ -n "$known_original" ]; then
        original="$known_original"
        backup="${BACKUP_DIR}/android.${original}.${TARGET_NAME}"
        [ -f "$backup" ] && [ "$(sha256_of "$backup")" = "$original" ] || {
            say_error "Резервная копия оригинала повреждена. Переключите язык в игре, чтобы клиент скачал файл заново."; return 1; }
        cp -f "$backup" "${WORK_DIR}/android/${TARGET_NAME}"
    else
        original="$current"
        backup="${BACKUP_DIR}/android.${original}.${TARGET_NAME}"
        if [ ! -f "$backup" ] || [ "$(sha256_of "$backup")" != "$original" ]; then
            cp -f "${WORK_DIR}/android/${TARGET_NAME}" "${backup}.tmp"
            [ "$(sha256_of "${backup}.tmp")" = "$original" ] || { rm -f "${backup}.tmp"; say_error "Резервная копия не сошлась, ничего не изменено."; return 1; }
            mv -f "${backup}.tmp" "$backup"
        fi
    fi

    progress_start "android: building"
    built="${WORK_DIR}/android/${TARGET_NAME}.ru"
    rm -f "$built" "${WORK_DIR}/android/report.json"
    if ! "$CLI" build --english "${WORK_DIR}/android/${ENGLISH_NAME}" --base "${WORK_DIR}/android/${TARGET_NAME}" \
            --translations "$OVERLAY_CACHE" --output "$built" --report "${WORK_DIR}/android/report.json" \
            --include-draft --raw --per-locale-content-version >>"$LOG_FILE" 2>&1; then
        say_error "Не удалось собрать перевод для версии игры на телефоне.

Скорее всего игра обновилась и перевод ещё не адаптирован."
        return 1
    fi
    applied="$(/usr/bin/sed -n 's/.*"applied_ru"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "${WORK_DIR}/android/report.json" | head -1)"

    "$ADB" -s "$serial" push "$built" "${ANDROID_DIR}/${TARGET_NAME}" >/dev/null 2>&1 || {
        say_error "Не удалось записать файл на телефон."; return 1; }

    local installed; installed="$(android_sha "$serial" "${ANDROID_DIR}/${TARGET_NAME}")"
    if [ "$installed" != "$(sha256_of "$built")" ]; then
        "$ADB" -s "$serial" push "$backup" "${ANDROID_DIR}/${TARGET_NAME}" >/dev/null 2>&1
        say_error "Установленный файл не прошёл проверку, оригинал возвращён."
        return 1
    fi

    cat > "$ANDROID_STATE" <<JSON
{
  "schema": 1,
  "device": "${serial}",
  "original_sha256": "${original}",
  "patched_sha256": "${installed}",
  "backup_path": "${backup}"
}
JSON

    say_info "Готово. На телефоне переведено строк: ${applied:-?}.

ЧТОБЫ ПЕРЕВОД НЕ ПРОПАЛ — то же правило, что и на компьютере:
не открывайте выбор языка в настройках игры. При выборе любого языка клиент заново скачивает файл и стирает перевод. Язык должен остаться украинским.

После обновления игры перевод нужно установить заново.
Вернуть оригинал можно кнопкой «Восстановить на телефоне»."
    return 0
}

android_restore() {
    local serial="$1" original backup current
    [ -f "$ANDROID_STATE" ] || { say_info "Перевод на телефон не устанавливался."; return 0; }
    original="$(json_field "$ANDROID_STATE" original_sha256)"
    backup="$(json_field "$ANDROID_STATE" backup_path)"
    [ -f "$backup" ] && [ "$(sha256_of "$backup")" = "$original" ] || {
        say_error "Резервная копия повреждена или отсутствует."; return 1; }

    current="$(android_sha "$serial" "${ANDROID_DIR}/${TARGET_NAME}")"
    [ "$current" != "$original" ] || { say_info "На телефоне уже оригинальный файл."; return 0; }

    "$ADB" -s "$serial" shell am force-stop "$ANDROID_PKG" >/dev/null 2>&1
    "$ADB" -s "$serial" push "$backup" "${ANDROID_DIR}/${TARGET_NAME}" >/dev/null 2>&1 || {
        say_error "Не удалось восстановить оригинал на телефоне."; return 1; }
    [ "$(android_sha "$serial" "${ANDROID_DIR}/${TARGET_NAME}")" = "$original" ] || {
        say_error "Восстановленный файл не прошёл проверку."; return 1; }
    say_info "На телефоне восстановлен оригинальный украинский текст."
    return 0
}

offer_adb_install() {
    local answer
    answer="$(ask "К компьютеру подключён телефон, но для работы с ним нужен ADB — стандартная утилита Google для связи с устройствами Android.

Её нет в системе. Установить через Homebrew одной командой?" "Не сейчас" "Установить")"
    [ "$answer" = "Установить" ] || return 1
    if ! command -v brew >/dev/null 2>&1; then
        say_error "Homebrew не установлен.

Скачайте Android Platform Tools вручную: developer.android.com/tools/releases/platform-tools"
        return 1
    fi
    say_info "Устанавливаю ADB, это займёт минуту. Нажмите OK и подождите."
    brew install --cask android-platform-tools >>"$LOG_FILE" 2>&1
    find_adb
}

# ---------- actions ----------

do_install() {
    local cache_root="$1" english target stamp built current original backup applied

    english="${cache_root}/dl_en_US.bin"
    target="${cache_root}/${TARGET_NAME}"
    stamp="${cache_root}/${TARGET_NAME}.ver"

    if [ ! -f "$target" ]; then
        say_error "Украинский языковой файл ещё не загружен.

Откройте игру, выберите в настройках украинский язык, дождитесь загрузки и полностью закройте игру. Затем запустите русификатор снова."
        return 1
    fi

    progress_start "downloading overlay"
    if ! refresh_overlay; then
        say_error "Не удалось загрузить перевод и нет сохранённой копии.

Проверьте интернет-соединение и попробуйте ещё раз."
        return 1
    fi

    progress_start "building"
    built="${WORK_DIR}/${TARGET_NAME}.ru"
    rm -f "$built" "${WORK_DIR}/report.json"
    # Applies every draft rather than only the conservative subset. Two thirds of the catalog carries
    # needs_review purely because identical English appears in several screens, which is a wording
    # nuance rather than a correctness problem; mechanically broken strings are already rejected at
    # import time and never reach the overlay.
    if ! "$CLI" build --english "$english" --base "$target" \
            --translations "$OVERLAY_CACHE" --output "$built" \
            --report "${WORK_DIR}/report.json" \
            --include-draft --raw --per-locale-content-version >>"$LOG_FILE" 2>&1; then
        say_error "Не удалось собрать перевод для этой версии игры.

Скорее всего игра обновилась и перевод ещё не адаптирован. Подробности: ${LOG_FILE}"
        return 1
    fi

    applied="$(/usr/bin/sed -n 's/.*"applied_ru"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "${WORK_DIR}/report.json" | head -1)"
    [ -n "$applied" ] || applied="?"

    current="$(sha256_of "$target")"
    if [ "$current" = "$(sha256_of "$built")" ]; then
        say_info "Перевод уже установлен и совпадает с актуальной сборкой."
        return 0
    fi

    # Decide what counts as the pristine original.
    local known_patched=""
    [ -f "$STATE_FILE" ] && known_patched="$(json_field "$STATE_FILE" patched_sha256 || true)"
    if [ -n "$known_patched" ] && [ "$current" = "$known_patched" ]; then
        original="$(json_field "$STATE_FILE" original_sha256)"
        backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
        if [ ! -f "$backup" ] || [ "$(sha256_of "$backup")" != "$original" ]; then
            say_error "Резервная копия оригинала повреждена или отсутствует.

Переустановите игру или переключите язык в игре, чтобы клиент скачал оригинальный файл заново."
            return 1
        fi
    else
        original="$current"
        backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
        if [ ! -f "$backup" ] || [ "$(sha256_of "$backup")" != "$original" ]; then
            if ! cat "$target" > "${backup}.tmp"; then rm -f "${backup}.tmp"; die "Не удалось создать резервную копию."; fi
            sync
            if [ "$(sha256_of "${backup}.tmp")" != "$original" ]; then
                rm -f "${backup}.tmp"
                say_error "Резервная копия не прошла проверку. Ничего не изменено."
                return 1
            fi
            mv -f "${backup}.tmp" "$backup"
        fi
    fi

    if ! atomic_install "$built" "$target"; then
        say_error "Не удалось записать файл перевода. Ничего не изменено."
        return 1
    fi

    local final; final="$(sha256_of "$target")"
    if [ "$final" != "$(sha256_of "$built")" ]; then
        cat "$backup" > "$target"
        say_error "Установленный файл не прошёл проверку, оригинал возвращён."
        return 1
    fi

    cat > "$STATE_FILE" <<JSON
{
  "schema": 1,
  "app_version": "${APP_VERSION}",
  "cache_root": "${cache_root}",
  "original_sha256": "${original}",
  "patched_sha256": "${final}",
  "backup_path": "${backup}"
}
JSON

    local next
    next="$(ask "Готово. Перевод установлен, строк переведено: ${applied}.

ЧТОБЫ ПЕРЕВОД НЕ ПРОПАЛ — одно правило:
Не открывайте выбор языка в настройках игры. При выборе любого языка клиент заново скачивает языковой файл с сервера и стирает перевод. В настройках должен остаться украинский: русский текст подставлен именно в эту ячейку, потому что она единственная кириллическая.

ЧТО ЕЩЁ ПОЛЕЗНО ЗНАТЬ:
• Часть текста останется на английском — это строки, которые не прошли проверку, их лучше видеть в оригинале, чем сломанными.
• Имена персонажей, боссов и локаций намеренно оставлены латиницей.
• После обновления игры перевод слетит: просто запустите русификатор снова и нажмите «Установить перевод».
• Если перевод вдруг исчез — почти всегда причина в том, что язык переключали. Установите заново.
• Вернуть английский или украинский текст можно в любой момент кнопкой «Восстановить оригинал» — оригинал сохранён.

Запустить игру сейчас?" "Закрыть" "Запустить игру")"
    if [ "$next" = "Запустить игру" ]; then
        open -b "hitzone.anima.spirit.guardians" >/dev/null 2>&1 \
            || open -a "Invokers" >/dev/null 2>&1 \
            || say_info "Не удалось запустить игру автоматически — откройте её вручную."
    fi
    return 0
}

do_restore() {
    local cache_root="$1" target original backup
    target="${cache_root}/${TARGET_NAME}"
    [ -f "$STATE_FILE" ] || { say_info "Русификатор ничего не изменял, восстанавливать нечего."; return 0; }
    original="$(json_field "$STATE_FILE" original_sha256)"
    backup="$(json_field "$STATE_FILE" backup_path)"
    if [ ! -f "$backup" ] || [ "$(sha256_of "$backup")" != "$original" ]; then
        say_error "Резервная копия повреждена или отсутствует.

Переключите язык в настройках игры — клиент скачает оригинальный файл заново."
        return 1
    fi
    if [ -f "$target" ] && [ "$(sha256_of "$target")" = "$original" ]; then
        say_info "В игре уже стоит оригинальный файл."
        return 0
    fi
    if ! atomic_install "$backup" "$target"; then
        say_error "Не удалось восстановить оригинал."
        return 1
    fi
    say_info "Оригинальный украинский текст восстановлен."
    return 0
}

describe_state() {
    local cache_root="$1" target current original patched line
    target="${cache_root}/${TARGET_NAME}"
    if [ ! -f "$target" ]; then
        printf 'Украинский языковой файл ещё не загружен игрой.'
        return
    fi
    current="$(sha256_of "$target")"
    if [ -f "$STATE_FILE" ]; then
        original="$(json_field "$STATE_FILE" original_sha256 || true)"
        patched="$(json_field "$STATE_FILE" patched_sha256 || true)"
        if [ "$current" = "$patched" ]; then line="Сейчас установлен: русский перевод."
        elif [ "$current" = "$original" ]; then line="Сейчас установлен: оригинальный украинский текст."
        else line="Файл изменился сам — скорее всего игра перекачала его после смены языка или обновления."
        fi
    else
        line="Перевод ещё не устанавливался."
    fi
    printf '%s' "$line"
}

# ---------- main ----------

[ -x "$CLI" ] || die "Повреждённая установка: не найден исполняемый файл внутри приложения."

# A relaunch triggered by the Full Disk Access prompt should land the user back where they were,
# not at the beginning of the same explanation they just read.
RESUMING=false
if [ -f "$RESUME_MARKER" ]; then
    marked="$(cat "$RESUME_MARKER" 2>/dev/null || echo 0)"
    now="$(date +%s)"
    [ $((now - marked)) -lt 600 ] 2>/dev/null && RESUMING=true
    rm -f "$RESUME_MARKER"
fi

self_update "$@"

if [ "$RESUMING" = false ]; then
    choice="$(ask "Неофициальный любительский русификатор Invokers: Titan Legacy.

Приложение не связано с HitZone Inc. Оно изменяет только один файл кэша локализации внутри папки данных игры и не трогает саму игру, её подпись и защиту. Оригинал сохраняется, откат доступен в любой момент.

Переведено 40 541 строка из 41 292 — почти весь интерфейс. Перевод машинный и не вычитан человеком. Используйте на свой риск." "Выход" "Продолжить")"
    [ "$choice" = "Продолжить" ] || exit 0
fi

check_app_update

CACHE_ROOT="$(find_cache_root || true)"
if [ -z "$CACHE_ROOT" ]; then
    # Without Full Disk Access the container is invisible rather than absent, so tell those two apart
    # before blaming the installation.
    if [ -d "${HOME}/Library/Containers" ] && ! ls "${HOME}/Library/Containers" >/dev/null 2>&1; then
        require_disk_access "${HOME}/Library/Containers"
    fi
    die "Не удалось найти данные игры Invokers.

Убедитесь, что игра установлена из App Store и была запущена хотя бы один раз.

Если игра точно установлена, скорее всего не выдан полный доступ к диску: «Системные настройки» → «Конфиденциальность и безопасность» → «Полный доступ к диску» → включить «Русификатор Invokers»."
fi
printf 'cache root: %s\n' "$CACHE_ROOT" >>"$LOG_FILE"
require_disk_access "${CACHE_ROOT}/dl_en_US.bin"

while true; do
    if game_running; then
        retry="$(ask "Игра сейчас запущена.

Полностью закройте Invokers (Cmd+Q), иначе изменения не сохранятся." "Выход" "Я закрыл, продолжить")"
        [ "$retry" = "Я закрыл, продолжить" ] || exit 0
        continue
    fi
    break
done

# A macOS dialog takes at most three buttons, so the platform is a separate question rather than a
# fourth button on the action menu.
ANDROID_SERIAL=""
if find_adb; then
    ANDROID_SERIAL="$(android_device)"
    [ -n "$ANDROID_SERIAL" ] && android_has_game "$ANDROID_SERIAL" || ANDROID_SERIAL=""
fi

PLATFORM="mac"
if [ -n "$ANDROID_SERIAL" ]; then
    where="$(ask "К компьютеру подключён телефон Android с установленной игрой.

Куда установить перевод?" "Отмена" "На телефон" "На этот Mac")"
    case "$where" in
        "На телефон") PLATFORM="android" ;;
        "На этот Mac") PLATFORM="mac" ;;
        *) exit 0 ;;
    esac
fi

if [ "$PLATFORM" = "android" ]; then
    action="$(ask "Телефон: ${ANDROID_SERIAL}

Что сделать?" "Отмена" "Восстановить на телефоне" "Установить на телефон")"
    case "$action" in
        "Установить на телефон") android_install "$ANDROID_SERIAL" ;;
        "Восстановить на телефоне") android_restore "$ANDROID_SERIAL" ;;
        *) exit 0 ;;
    esac
    exit 0
fi

action="$(ask "$(describe_state "$CACHE_ROOT")

Что сделать?" "Отмена" "Восстановить оригинал" "Установить перевод")"

case "$action" in
    "Установить перевод") do_install "$CACHE_ROOT" ;;
    "Восстановить оригинал") do_restore "$CACHE_ROOT" ;;
    *) exit 0 ;;
esac
