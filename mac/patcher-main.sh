#!/bin/bash
# Driver for InvokersRu Patcher.app — every message reaches the user through a native dialog,
# because a bundle launched from Finder has nowhere to print to.

set -uo pipefail

APP_VERSION="1.0.0"
REPO_RAW="https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main"
OVERLAY_URL="${REPO_RAW}/translations/ru_RU.jsonl"
MANIFEST_URL="${REPO_RAW}/config/mac-patcher.json"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESOURCES="$(cd "${HERE}/../Resources" && pwd)"
CLI="${RESOURCES}/InvokersRu.Cli"

SUPPORT_DIR="${HOME}/Library/Application Support/InvokersRu"
WORK_DIR="${SUPPORT_DIR}/work"
BACKUP_DIR="${SUPPORT_DIR}/backups"
STATE_FILE="${SUPPORT_DIR}/state.json"
OVERLAY_CACHE="${SUPPORT_DIR}/ru_RU.jsonl"
LOG_FILE="${SUPPORT_DIR}/patcher.log"

TARGET_NAME="dl_uk_UA.bin"
TITLE="Русификатор Invokers"

mkdir -p "$SUPPORT_DIR" "$WORK_DIR" "$BACKUP_DIR"
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

fetch() { curl -fsSL --max-time 30 "$1" -o "$2" 2>>"$LOG_FILE"; }

check_app_update() {
    local manifest="${WORK_DIR}/manifest.json" latest notes
    fetch "$MANIFEST_URL" "$manifest" || return 0
    latest="$(json_field "$manifest" app_version || true)"
    [ -n "$latest" ] || return 0
    if [ "$latest" != "$APP_VERSION" ]; then
        notes="$(json_field "$manifest" notes || true)"
        say_info "Доступна новая версия русификатора: ${latest} (у вас ${APP_VERSION}).

${notes}

Скачать можно со страницы проекта на GitHub."
    fi
}

# Downloads the public overlay. Returns 0 if a usable overlay is at $OVERLAY_CACHE.
refresh_overlay() {
    local fresh="${WORK_DIR}/ru_RU.jsonl.new"
    if fetch "$OVERLAY_URL" "$fresh" && [ -s "$fresh" ]; then
        if [ ! -f "$OVERLAY_CACHE" ] || [ "$(sha256_of "$fresh")" != "$(sha256_of "$OVERLAY_CACHE")" ]; then
            mv -f "$fresh" "$OVERLAY_CACHE"
            printf 'overlay updated\n' >>"$LOG_FILE"
        else
            rm -f "$fresh"
        fi
        return 0
    fi
    rm -f "$fresh"
    [ -s "$OVERLAY_CACHE" ]
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
    if ! "$CLI" build --english "$english" --base "$target" \
            --translations "$OVERLAY_CACHE" --output "$built" \
            --report "${WORK_DIR}/report.json" \
            --include-draft --exclude-needs-review --raw --per-locale-content-version >>"$LOG_FILE" 2>&1; then
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

    say_info "Готово. Переведено строк: ${applied}.

ВАЖНО, иначе перевод пропадёт:
• Не меняйте язык в настройках игры. Клиент при выборе языка заново скачивает файл и стирает перевод.
• Язык должен остаться украинским — русский текст подставлен именно в него.
• После обновления игры перевод нужно установить заново.

Оригинал сохранён, вернуть его можно кнопкой «Восстановить оригинал»."
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

choice="$(ask "Неофициальный любительский русификатор Invokers: Titan Legacy.

Приложение не связано с HitZone Inc. Оно изменяет только один файл кэша локализации внутри папки данных игры и не трогает саму игру, её подпись и защиту. Оригинал сохраняется, откат доступен в любой момент.

Перевод неполный: переведена часть интерфейса, остальное останется на английском. Используйте на свой риск." "Выход" "Продолжить")"
[ "$choice" = "Продолжить" ] || exit 0

check_app_update

CACHE_ROOT="$(find_cache_root || true)"
if [ -z "$CACHE_ROOT" ]; then
    die "Не удалось найти данные игры Invokers.

Убедитесь, что игра установлена из App Store и была запущена хотя бы один раз."
fi
printf 'cache root: %s\n' "$CACHE_ROOT" >>"$LOG_FILE"

while true; do
    if game_running; then
        retry="$(ask "Игра сейчас запущена.

Полностью закройте Invokers (Cmd+Q), иначе изменения не сохранятся." "Выход" "Я закрыл, продолжить")"
        [ "$retry" = "Я закрыл, продолжить" ] || exit 0
        continue
    fi
    break
done

action="$(ask "$(describe_state "$CACHE_ROOT")

Что сделать?" "Отмена" "Восстановить оригинал" "Установить перевод")"

case "$action" in
    "Установить перевод") do_install "$CACHE_ROOT" ;;
    "Восстановить оригинал") do_restore "$CACHE_ROOT" ;;
    *) exit 0 ;;
esac
