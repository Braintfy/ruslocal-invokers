#!/bin/bash
# Driver for InvokersRu Patcher.app — every message reaches the user through a native dialog,
# because a bundle launched from Finder has nowhere to print to.

set -uo pipefail

# Version of this script. It updates itself from the repository; the bundle around it stays frozen.
APP_VERSION="2.11.0"
# Version of the application bundle, which only changes when the launcher or the CLI has to change.
BUNDLE_VERSION="3.1.0"
# Oldest bundle that still works. Kept apart from BUNDLE_VERSION so rebuilding the image does not tell
# everyone to download it again: a new bundle is only mandatory when the old one genuinely cannot run.
MINIMUM_BUNDLE_VERSION="3.0.0"

REPO_RAW="https://raw.githubusercontent.com/Braintfy/ruslocal-invokers/main"
OVERLAY_URL="${REPO_RAW}/translations/ru_RU.jsonl"
MANIFEST_URL="${REPO_RAW}/config/mac-patcher.json"
PATCHER_URL="${REPO_RAW}/mac/patcher-main.sh"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Set by the launcher, because this script may be running from the updatable copy outside the bundle.
RESOURCES="${INVOKERSRU_RESOURCES:-$HERE}"
CLI="${RESOURCES}/InvokersRu.Cli"

SUPPORT_DIR="${INVOKERSRU_SUPPORT_DIR:-${HOME}/Library/Application Support/InvokersRu}"
WORK_DIR="${SUPPORT_DIR}/work"
BACKUP_DIR="${SUPPORT_DIR}/backups"
RUNTIME_DIR="${SUPPORT_DIR}/runtime"
LEGACY_STATE_FILE="${SUPPORT_DIR}/state.json"
STATE_DIR="${SUPPORT_DIR}/states"
STATE_FILE="$LEGACY_STATE_FILE"
OVERLAY_CACHE="${SUPPORT_DIR}/ru_RU.jsonl"
LOG_FILE="${SUPPORT_DIR}/patcher.log"
RESUME_MARKER="${SUPPORT_DIR}/.resuming"

TARGET_NAME="dl_uk_UA.bin"
ENGLISH_NAME="dl_en_US.bin"
TITLE="Русификатор Invokers"

mkdir -p "$SUPPORT_DIR" "$WORK_DIR" "$BACKUP_DIR" "$RUNTIME_DIR" "$STATE_DIR"
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

# Numeric fields from the build report. json_field only reads quoted strings, and every number that
# explains a build — how much applied, how much went stale — is unquoted.
report_number() {
    [ -f "$1" ] || return 1
    /usr/bin/sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\([0-9-]\{1,\}\).*/\1/p" "$1" | head -1
}

# What the game itself says about a table. The driver used to guess that "the game probably updated";
# reading the version is the difference between a guess and a reason.
inspect_field() {
    local file="$1" field="$2" tmp="${WORK_DIR}/inspect.json"
    [ -f "$file" ] || return 1
    "$CLI" inspect "$file" >"$tmp" 2>/dev/null || return 1
    json_field "$tmp" "$field"
}

json_field() {
    [ -f "$1" ] || return 1
    /usr/bin/sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$1" | head -1
}

# ---------- environment ----------

# Every place a build of the game is known to keep its localization cache. The native desktop client
# introduced in 0.60.1289 writes to Application Support, while the retired iOS-on-Mac client hides its
# cache in a container named after a random UUID. Both may remain on one Mac, so cache selection
# explicitly prefers the native location and only searches older paths when it is absent.
cache_candidates() {
    local containers="${HOME}/Library/Containers" container candidate explicit

    explicit="${INVOKERSRU_CACHE_ROOT:-}"
    if [ -n "$explicit" ] && [ -f "${explicit}/${ENGLISH_NAME}" ]; then
        printf '%s\n' "$explicit"
    fi

    # Current standalone launcher (bundle id) and Unity's documented/fallback persistent-data paths.
    for candidate in "${HOME}/Library/Application Support/hitzone.anima.spirit.guardians/i18n" \
                     "${HOME}/Library/Application Support/unity.Hit.Zone.Invokers/i18n" \
                     "${HOME}/Library/Application Support/Hit.Zone/Invokers/i18n" \
                     "${HOME}/Library/Application Support/Hit_Zone/Invokers/i18n" \
                     "${HOME}/Library/Application Support/com.Hit_Zone.Invokers/i18n"; do
        [ -f "${candidate}/${ENGLISH_NAME}" ] && printf '%s\n' "$candidate"
    done

    if [ -d "$containers" ]; then
        for container in "$containers"/*/; do
            candidate="${container}Data/Documents/i18n"
            [ -f "${candidate}/${ENGLISH_NAME}" ] && printf '%s\n' "$candidate"
        done
    fi
}

cache_version() {
    local stamp="$1/${ENGLISH_NAME}.ver" family
    # 0.61 removed the downloaded .ver sidecars and uses a versioned LOC1 family.
    # Read the downloaded source, not a bundled fallback that may no longer be active.
    family="$(inspect_field "$1/${ENGLISH_NAME}" content_guid || true)"
    if [[ "$family" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
        printf '%s' "$family"
        return 0
    fi
    [ -r "$stamp" ] && tr -d '\r\n' < "$stamp" 2>/dev/null || printf '0'
}

cache_display_version() {
    local english target
    english="$(inspect_field "$1/${ENGLISH_NAME}" content_version || true)"
    target="$(inspect_field "$1/${TARGET_NAME}" content_version || true)"
    if [ -n "$english" ] && [ -n "$target" ] && [ "$english" != "$target" ]; then
        printf 'EN %s / UK %s' "$english" "$target"
    elif [ -n "$english" ]; then
        printf '%s' "$english"
    else
        cache_version "$1"
    fi
}

matching_downloaded_tables() {
    local cache_root="$1" english_guid target_guid english_version target_version
    local numeric_version='^Prod_([0-9]+\.[0-9]+\.[0-9]+)_([0-9]+)$'
    english_guid="$(inspect_field "$cache_root/${ENGLISH_NAME}" content_guid || true)"
    target_guid="$(inspect_field "$cache_root/${TARGET_NAME}" content_guid || true)"
    [ -n "$english_guid" ] && [ "$english_guid" = "$target_guid" ] || return 1
    # The 0.61+ tables may have independent EN/UK revisions (for example EN Prod_0.61.1_3
    # with UK Prod_0.61.1_5), but each must identify the same LOC1 content family.
    # Older UUID families could legitimately use a different content-version format.
    if [[ "$english_guid" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        english_version="$(inspect_field "$cache_root/${ENGLISH_NAME}" content_version || true)"
        target_version="$(inspect_field "$cache_root/${TARGET_NAME}" content_version || true)"
        [[ "$english_version" =~ $numeric_version ]] || return 1
        [ "${BASH_REMATCH[1]}" = "$english_guid" ] || return 1
        [[ "$target_version" =~ $numeric_version ]] || return 1
        [ "${BASH_REMATCH[1]}" = "$target_guid" ] || return 1
    fi
}

cache_priority() {
    case "$1" in
        "${HOME}/Library/Application Support/hitzone.anima.spirit.guardians/i18n") printf '40' ;;
        "${HOME}/Library/Application Support/"*) printf '30' ;;
        "${HOME}/Library/Containers/"*) printf '20' ;;
        *) printf '10' ;;
    esac
}

cache_modified() {
    stat -f '%m' "$1/${ENGLISH_NAME}.ver" 2>/dev/null \
        || stat -f '%m' "$1/${ENGLISH_NAME}" 2>/dev/null \
        || printf '0'
}

# The list is collected once and counted in memory. Piping into head instead would close the pipe
# early, and under pipefail that turns a successful lookup into a non-zero exit status.
cache_candidate_count() {
    local list
    list="$(cache_candidates)"
    [ -n "$list" ] || { printf '0\n'; return 0; }
    printf '%s\n' "$list" | wc -l | tr -d ' '
}

find_cache_root() {
    local list candidate version priority modified
    local best="" best_version="0" best_priority="0" best_modified="0"
    if [ -n "${INVOKERSRU_CACHE_ROOT:-}" ] \
        && [ -f "${INVOKERSRU_CACHE_ROOT}/${ENGLISH_NAME}" ]; then
        printf '%s\n' "$INVOKERSRU_CACHE_ROOT"
        return 0
    fi
    # A retired iOS container is not a candidate while the native desktop client has its own cache.
    # Its version may be higher due to an unrelated App Store build, but the native game never reads it.
    candidate="${HOME}/Library/Application Support/hitzone.anima.spirit.guardians/i18n"
    if [ -f "${candidate}/${ENGLISH_NAME}" ]; then
        printf '%s\n' "$candidate"
        return 0
    fi
    list="$(cache_candidates | awk '!seen[$0]++')"
    [ -n "$list" ] || return 1
    while IFS= read -r candidate; do
        [ -n "$candidate" ] || continue
        version="$(cache_version "$candidate")"
        priority="$(cache_priority "$candidate")"
        modified="$(cache_modified "$candidate")"
        if [ -z "$best" ] \
            || version_older "$best_version" "$version" \
            || { ! version_older "$version" "$best_version" \
                 && ! version_older "$best_version" "$version" \
                 && { [ "$priority" -gt "$best_priority" ] \
                      || { [ "$priority" -eq "$best_priority" ] && [ "$modified" -gt "$best_modified" ]; }; }; }; then
            best="$candidate"
            best_version="$version"
            best_priority="$priority"
            best_modified="$modified"
        fi
    done <<<"$list"
    [ -n "$best" ] || return 1
    printf '%s\n' "$best"
}

select_state_file() {
    local cache_root="$1" key legacy_root
    key="$(printf '%s' "$cache_root" | shasum -a 256 | awk '{print toupper($1)}')"
    STATE_FILE="${STATE_DIR}/${key}.json"
    if [ ! -f "$STATE_FILE" ] && [ -f "$LEGACY_STATE_FILE" ]; then
        legacy_root="$(json_field "$LEGACY_STATE_FILE" cache_root || true)"
        if [ "$legacy_root" = "$cache_root" ]; then
            cp -f "$LEGACY_STATE_FILE" "${STATE_FILE}.tmp" 2>/dev/null \
                && mv -f "${STATE_FILE}.tmp" "$STATE_FILE"
        fi
    fi
}

game_running() {
    pgrep -f 'Invokers\.app/(Contents/MacOS/)?Invokers([[:space:]]|$)' >/dev/null 2>&1
}

preferred_language() {
    # Unity stores the selected LOC1 locale id here. Read only this scalar: the same plist also holds
    # account credentials and must never be copied into diagnostics or logs.
    defaults read hitzone.anima.spirit.guardians 'i18n.Language' 2>/dev/null || true
}

activate_ukrainian_language() {
    # The standalone launcher passes its own locale to Unity. Persist slot 8 as well so a direct launch
    # from this app and the next game session both read the file that was just installed.
    defaults write hitzone.anima.spirit.guardians 'i18n.Language' -int 8 2>>"$LOG_FILE"
}

# Reports the hardware, not the process: under Rosetta uname would answer x86_64 on an Apple Silicon
# machine, and the whole diagnosis below hangs on telling those two apart correctly.
apple_silicon() { [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = "1" ]; }

cpu_name() { sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -m; }

# Spotlight finds the game wherever it was moved and needs no permission of its own; the fixed path is
# the fallback for a machine with indexing switched off.
game_bundle() {
    local hit
    hit="${HOME}/Library/Application Support/zone.hitzone.invokers.launcher/game/Invokers.app"
    [ -d "$hit" ] && { printf '%s\n' "$hit"; return 0; }
    while IFS= read -r hit; do
        if [ -x "$hit/Contents/MacOS/Invokers" ]; then
            printf '%s\n' "$hit"
            return 0
        fi
    done < <(mdfind "kMDItemCFBundleIdentifier == 'hitzone.anima.spirit.guardians'" 2>/dev/null)
    [ -d "/Applications/Invokers Titan Legacy.app" ] && { printf '%s\n' "/Applications/Invokers Titan Legacy.app"; return 0; }
    [ -d "/Applications/Invokers.app" ] && { printf '%s\n' "/Applications/Invokers.app"; return 0; }
    return 1
}

# A precaution, not a promise about account bans: server-side rules and unknown protections cannot
# be detected locally. Only inspect the selected localization cache and the discovered Invokers app;
# ordinary app signing (_CodeSignature/CodeResources), .ver and .src are not protection evidence.
# Do not follow symlinks into unrelated applications or system directories.
protection_reason() {
    local cache_root="$1" marker bundle found
    if [ ! -r "$cache_root" ] || [ ! -x "$cache_root" ] || [ -L "$cache_root" ]; then
        printf 'Не удалось проверить папку локализации: %s' "$cache_root"
        return 0
    fi
    for marker in "$ENGLISH_NAME" "$TARGET_NAME" "${TARGET_NAME}.ver"; do
        if [ -L "${cache_root}/${marker}" ]; then
            printf 'Языковой файл является ссылкой и не может быть проверен: %s/%s' "$cache_root" "$marker"
            return 0
        fi
    done
    found="$(find "$cache_root" -type d ! -path "$cache_root" -prune -o -print 2>>"$LOG_FILE" | LC_ALL=C awk '
        function sidecar(name) {
            return name ~ /^(dl_)?(en_us|uk_ua)\.bin(\.(br|gz))?(\.(ver|src))?\.(sig|signature|sha256|sha512|hmac|p7s|manifest|checksum)$/ ||
                   name ~ /^(localization|i18n)[._-](manifest|checksums?|signatures?)(\.json)?(\.(sig|signature|sha256|sha512|hmac|p7s|manifest|checksum))?$/
        }
        { name=$0; sub(/^.*\//, "", name); if (!first && sidecar(tolower(name))) first=$0 }
        END { if (first) print first }
    ')" || { printf 'Не удалось полностью проверить папку локализации: %s' "$cache_root"; return 0; }
    if [ -n "$found" ]; then
        printf 'Обнаружен файл проверки локализации: %s' "$found"
        return 0
    fi

    bundle="$(game_bundle || true)"
    [ -n "$bundle" ] || return 0
    if [ -L "$bundle" ] || [ ! -r "$bundle" ] || [ ! -x "$bundle" ]; then
        printf 'Не удалось проверить найденное приложение игры: %s' "$bundle"
        return 0
    fi
    # find walks only this app, never the disk. awk consumes the complete list so unreadable
    # subdirectories produce a failure instead of a partial scan being reported as successful.
    found="$(find "$bundle" -print 2>>"$LOG_FILE" | LC_ALL=C awk '
        function anticheat(name) {
            return name == "easyanticheat" || name == "easyanticheat_eos" || name ~ /^easyanticheat.*\.(exe|dll|sys|dylib|so)$/ ||
                   name == "battleye" || name ~ /^beservice.*\.exe$/ || name == "bedaisy.sys" ||
                   name ~ /^beclient.*\.(dll|dylib|so)$/ || name ~ /^lib(easyanticheat|beclient).*\.(dylib|so)$/ ||
                   name ~ /^equ8/ || name ~ /^xigncode/ || name ~ /^xhunter.*\.sys$/ || name == "x3.xem" ||
                   name ~ /^ace-base/ || name == "anticheatexpert" ||
                   name ~ /^anticheat\.(exe|dll|sys|dylib|so)$/ || name == "gameguard" || name == "gameguard.des"
        }
        { name=$0; sub(/^.*\//, "", name); if (!first && anticheat(tolower(name))) first=$0 }
        END { if (first) print first }
    ')" || { printf 'Не удалось полностью проверить файлы игры: %s' "$bundle"; return 0; }
    if [ -n "$found" ]; then
        printf 'Обнаружен компонент защиты игры: %s' "$found"
        return 0
    fi
}

require_patch_preflight() {
    local reason
    if game_running; then
        say_error "Игра была запущена во время подготовки. Полностью закройте игру и лаунчер, затем повторите установку."
        return 1
    fi
    reason="$(protection_reason "$1")" || reason="Не удалось завершить проверку файлов игры."
    [ -n "$reason" ] || return 0
    printf 'PROTECTION_CHECK_BLOCKED: %s\n' "$reason" >>"$LOG_FILE"
    say_error "Установка перевода остановлена.

${reason}

Изменение локализации при наличии защиты может привести к блокировке аккаунта. Не удаляйте эти файлы и не отключайте защиту. Дождитесь проверки совместимости; если перевод уже установлен, закройте игру и воспользуйтесь «Восстановить оригинал».

Проверка локальных файлов не может гарантировать отсутствие банов или обнаружить серверные ограничения."
    return 1
}

launch_game() {
    local native_game="${HOME}/Library/Application Support/zone.hitzone.invokers.launcher/game/Invokers.app"
    if [ -d "$native_game" ]; then
        activate_ukrainian_language || true
        open -n "$native_game" --args -language uk_UA >/dev/null 2>&1 && return 0
    fi
    open -a "Invokers Titan Legacy" >/dev/null 2>&1 && return 0
    open -b "hitzone.anima.spirit.guardians" >/dev/null 2>&1 && return 0
    open -a "Invokers" >/dev/null 2>&1
}

# The published bundle contains both Mac architectures. Checking the handler here turns a damaged or
# incomplete image into a useful message instead of leaking a shell error into the UI.
require_cli() {
    [ -f "$CLI" ] || die "Внутри приложения нет файла обработчика (${CLI}).

Образ повреждён — скачайте его заново со страницы проекта."
    chmod +x "$CLI" 2>/dev/null || true
    "$CLI" help >/dev/null 2>&1 && return 0
    die "Встроенный обработчик файлов игры не запускается на этом Mac.

Скачайте образ приложения заново со страницы проекта. Процессор: $(cpu_name)."
}

# One message per real cause. The old dialog blamed Full Disk Access for every empty result, which is
# wrong in the common cases: on a machine that cannot run the game at all no permission will ever help,
# and a game that has never been launched has no data to protect in the first place.
diagnose_missing_game() {
    local bundle count
    bundle="$(game_bundle || true)"
    count="$(cache_candidate_count)"

    if [ "$count" -gt 1 ]; then
        die "Найдено несколько папок с данными игры (${count}).

Русификатор не станет угадывать, какую из них менять, чтобы не испортить чужие файлы. Напишите об этом в issue проекта — путь нужно будет указать вручную."
    fi

    if [ -z "$bundle" ]; then
        die "Игра Invokers на этом Mac не найдена.

Установите официальный клиент «Invokers Titan Legacy» с сайта invokers.com, запустите лаунчер, скачайте игру и откройте её хотя бы один раз, чтобы она создала папку с данными.

Затем запустите русификатор снова."
    fi

    if [ -d "${HOME}/Library/Containers" ] && ! ls "${HOME}/Library/Containers" >/dev/null 2>&1; then
        require_disk_access "${HOME}/Library/Containers"
        return 0
    fi

    die "Игра установлена, но её языковые файлы ещё не скачаны.

Запустите Invokers, дождитесь главного меню, зайдите в настройки и выберите украинский язык. Дождитесь загрузки и полностью закройте игру (Cmd+Q).

До этого заменять нечего: игра держит языковые таблицы не внутри себя, а в папке данных, и создаёт их при первом запуске.

Игра найдена здесь: ${bundle}"
}

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
fetch_update() { curl -fsSL --compressed --connect-timeout 5 --max-time 15 "$1" -o "$2" 2>>"$LOG_FILE"; }

# Replaces this script from the repository without touching the application bundle, so the Full Disk
# Access grant — which macOS pins to the bundle's code signature — keeps working. The download is
# accepted only when its SHA-256 matches the value published in the manifest.
self_update() {
    [ -z "${INVOKERSRU_UPDATED:-}" ] || return 0
    [ ! -f "${SUPPORT_DIR}/no-self-update" ] || return 0

    local manifest="${WORK_DIR}/manifest.json" published expected fresh
    fetch_update "$MANIFEST_URL" "$manifest" || return 0
    published="$(json_field "$manifest" patcher_version || true)"
    expected="$(json_field "$manifest" patcher_sha256 || true)"
    [ -n "$published" ] && [ -n "$expected" ] || return 0
    [ "$published" != "$APP_VERSION" ] || return 0

    # A published version older than the one already running means the repository has not caught up
    # with a release yet. Following it downgrades a working driver to an obsolete one — which is what a
    # freshly released image does on its very first launch, silently undoing everything the release was
    # for. Rolling back stays possible, but it has to be stated in the manifest instead of happening by
    # accident.
    if version_older "$published" "$APP_VERSION" \
       && [ "$(json_field "$manifest" allow_downgrade || true)" != "yes" ]; then
        printf 'self-update refused: published %s is older than %s\n' "$published" "$APP_VERSION" >>"$LOG_FILE"
        return 0
    fi

    fresh="${WORK_DIR}/patcher.sh.new"
    fetch_update "$PATCHER_URL" "$fresh" || { rm -f "$fresh"; return 0; }
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
    if [ "${1:-}" != "--gui-status" ]; then
        say_info "Русификатор обновлён: ${APP_VERSION} → ${published}.

$(json_field "$manifest" notes || true)

Переустанавливать приложение и заново выдавать доступ к диску не нужно."
    fi
    INVOKERSRU_UPDATED=1 exec /bin/bash "${RUNTIME_DIR}/patcher.sh" "$@"
}

# Compares dotted numbers rather than strings, so a bundle newer than the published minimum is not
# mistaken for an outdated one.
version_older() {
    local -a left right
    local index a b
    IFS=. read -r -a left <<<"$1"
    IFS=. read -r -a right <<<"$2"
    for index in 0 1 2; do
        a="${left[$index]:-0}"; b="${right[$index]:-0}"
        a="${a//[!0-9]/}"; b="${b//[!0-9]/}"
        [ -n "$a" ] || a=0
        [ -n "$b" ] || b=0
        [ "$a" -lt "$b" ] && return 0
        [ "$a" -gt "$b" ] && return 1
    done
    return 1
}

check_app_update() {
    local manifest="${WORK_DIR}/manifest.json" required notes
    [ -f "$manifest" ] || fetch "$MANIFEST_URL" "$manifest" || return 0
    required="$(json_field "$manifest" minimum_bundle_version || true)"
    [ -n "$required" ] || return 0
    if version_older "$BUNDLE_VERSION" "$required"; then
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

# Names which of the two tables moved, because that is what decides what the player can do about it.
# A changed Ukrainian table is harmless — the translation is keyed to English — while a changed English
# table means the game rewrote its own text and those rows genuinely have no translation yet.
explain_build_failure() {
    local english="$1" base="$2" ev bv reason
    ev="$(inspect_field "$english" content_version || true)"; [ -n "$ev" ] || ev="не прочитана"
    bv="$(inspect_field "$base" content_version || true)"; [ -n "$bv" ] || bv="не прочитана"
    reason="$(grep -i 'ERROR' "$LOG_FILE" 2>/dev/null | tail -1)"
    [ -n "$reason" ] || reason="$(tail -1 "$LOG_FILE" 2>/dev/null)"
    say_error "Не удалось собрать перевод.

Английская таблица игры: ${ev}
Украинская таблица игры: ${bv}

${reason}

После крупного обновления сначала выберите английский язык и полностью перезапустите игру. Дождитесь главного меню, затем повторите это с украинским языком и закройте игру. Так клиент загрузит обе свежие таблицы.

Если ошибка остается, отправьте автору эти версии и журнал: ${LOG_FILE}"
}

restore_available() {
    local target="$1" original patched recorded_backup expected_backup recorded_root
    [ -f "$STATE_FILE" ] && [ -f "$target" ] || return 1
    original="$(json_field "$STATE_FILE" original_sha256 || true)"
    patched="$(json_field "$STATE_FILE" patched_sha256 || true)"
    recorded_backup="$(json_field "$STATE_FILE" backup_path || true)"
    recorded_root="$(json_field "$STATE_FILE" cache_root || true)"
    [[ "$original" =~ ^[A-F0-9]{64}$ && "$patched" =~ ^[A-F0-9]{64}$ ]] || return 1
    [ "$recorded_root" = "$(dirname "$target")" ] || return 1
    expected_backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
    [ "$recorded_backup" = "$expected_backup" ] || return 1
    [ "$(sha256_of "$target")" = "$patched" ] || return 1
    [ -f "$expected_backup" ] && [ "$(sha256_of "$expected_backup")" = "$original" ]
}

# A build can succeed and still leave English on screen: that happens when the game rewrites strings it
# had before, which invalidates the rows translated from the old wording. Saying so up front beats
# letting the player find it mid-fight and assume the patcher broke.
composition_note() {
    local report="$1" applied stale missing left
    applied="$(report_number "$report" applied_ru)"
    [ -n "$applied" ] || { printf 'Перевод установлен.'; return 0; }
    stale="$(report_number "$report" stale_catalog)"; [ -n "$stale" ] || stale=0
    missing="$(report_number "$report" missing_catalog)"; [ -n "$missing" ] || missing=0
    left=$((stale + missing))
    if [ "$left" -gt 50 ]; then
        printf 'Переведено строк: %s.\n\nАнглийскими остались %s — игра изменила эти тексты после того, как их перевели. Перевод для них появится в следующем обновлении каталога.' "$applied" "$left"
    else
        printf 'Переведено строк: %s.' "$applied"
    fi
}

do_install() {
    local cache_root="$1" english target built current original backup applied english_sha
    local known_patched="" saved_english=""

    english="${cache_root}/${ENGLISH_NAME}"
    target="${cache_root}/${TARGET_NAME}"

    if [ ! -f "$target" ]; then
        say_error "Украинский языковой файл ещё не загружен.

Откройте игру, выберите в настройках украинский язык, дождитесь загрузки и полностью закройте игру. Затем запустите русификатор снова."
        return 1
    fi

    if ! matching_downloaded_tables "$cache_root"; then
        say_error "Английская и украинская таблицы относятся к разным выпускам игры.

Запустите игру сначала с английским, затем с украинским языком, каждый раз дождавшись главного меню. Полностью закройте игру и повторите установку."
        return 1
    fi

    require_patch_preflight "$cache_root" || return 1
    english_sha="$(sha256_of "$english")"
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
        explain_build_failure "$english" "$target"
        return 1
    fi

    applied="$(report_number "${WORK_DIR}/report.json" applied_ru)"
    [ -n "$applied" ] || applied="?"

    current="$(sha256_of "$target")"
    [ -f "$STATE_FILE" ] && known_patched="$(json_field "$STATE_FILE" patched_sha256 || true)"
    if [ -n "$known_patched" ] && [ "$current" = "$known_patched" ]; then
        saved_english="$(json_field "$STATE_FILE" english_sha256 || true)"
        if [ -n "$saved_english" ] && [ "$saved_english" != "$english_sha" ]; then
            say_error "Английская таблица игры изменилась, а украинский файл всё ещё содержит перевод предыдущего выпуска.

Обновите украинский язык в игре и полностью закройте её перед установкой нового перевода."
            return 1
        fi
    fi
    if [ "$current" = "$(sha256_of "$built")" ]; then
        if restore_available "$target"; then
            say_info "Перевод уже установлен и совпадает с актуальной сборкой."
            return 0
        fi
        say_error "Русский файл уже находится в кеше, но у русификатора нет проверенной резервной копии для него.

Файл не изменён. Чтобы создать безопасную точку восстановления, обновите украинский язык в игре, полностью закройте её и установите перевод снова."
        return 1
    fi

    # Decide what counts as the pristine original.
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

    # Recheck after downloads/build/backup: the launcher may have added protection in that time.
    require_patch_preflight "$cache_root" || return 1
    if [ "$(sha256_of "$english")" != "$english_sha" ] || [ "$(sha256_of "$target")" != "$current" ]; then
        say_error "Языковые файлы изменились во время сборки. Ничего не записано; закройте игру и повторите установку."
        return 1
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
  "english_sha256": "${english_sha}",
  "original_sha256": "${original}",
  "patched_sha256": "${final}",
  "backup_path": "${backup}"
}
JSON

    if ! activate_ukrainian_language; then
        printf 'warning: could not persist Ukrainian language slot\n' >>"$LOG_FILE"
    fi

    local next
    next="$(ask "Готово. $(composition_note "${WORK_DIR}/report.json")

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
        launch_game || say_info "Не удалось запустить игру автоматически — откройте её вручную."
    fi
    return 0
}

do_restore() {
    local cache_root="$1" target original backup current saved_english current_english
    target="${cache_root}/${TARGET_NAME}"
    [ -f "$STATE_FILE" ] || { say_info "Русификатор ничего не изменял, восстанавливать нечего."; return 0; }
    original="$(json_field "$STATE_FILE" original_sha256 || true)"
    current="$(sha256_of "$target")"
    if [ -n "$original" ] && [ "$current" = "$original" ]; then
        say_info "В игре уже стоит оригинальный файл."
        return 0
    fi
    if ! restore_available "$target"; then
        say_error "Текущий языковой файл не совпадает с переводом, установленным этим русификатором, либо его резервная копия недоступна.

Возможно, клиент уже загрузил новую официальную версию. Старую копию поверх неё записывать нельзя. Проверьте состояние игры или установите перевод для текущей версии заново."
        return 1
    fi
    backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
    if game_running; then
        say_error "Полностью закройте игру и лаунчер перед восстановлением оригинала."
        return 1
    fi
    if [ "$(sha256_of "$target")" != "$(json_field "$STATE_FILE" patched_sha256)" ]; then
        say_error "Языковой файл изменился во время восстановления. Ничего не записано."
        return 1
    fi
    if ! atomic_install "$backup" "$target"; then
        say_error "Не удалось восстановить оригинал."
        return 1
    fi
    if [ "$(sha256_of "$target")" != "$original" ]; then
        say_error "Восстановленный файл не прошёл проверку. Не запускайте игру до повторной загрузки украинского языка."
        return 1
    fi
    saved_english="$(json_field "$STATE_FILE" english_sha256 || true)"
    current_english="$(sha256_of "${cache_root}/${ENGLISH_NAME}")"
    if [ -n "$saved_english" ] && [ "$saved_english" != "$current_english" ]; then
        say_info "Резервная копия оригинального украинского текста восстановлена, но английская таблица игры уже обновилась.

Откройте игру и заново загрузите украинский язык, чтобы оба официальных файла соответствовали одной версии."
    else
        say_info "Оригинальный украинский текст восстановлен."
    fi
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

# ---------- native window bridge ----------

gui_emit() {
    local key="$1" value="$2"
    value="$(printf '%s' "$value" | tr '\r\n' '  ')"
    printf '%s=%s\n' "$key" "$value"
}

gui_status() {
    local cache_root target version client state title detail current original patched running="no"
    local language language_label can_install="no" can_restore="no" protection
    local saved_english tables_match="yes"
    cache_root="$(find_cache_root || true)"
    game_running && running="yes"
    language="$(preferred_language)"
    case "$language" in
        8) language_label="украинский" ;;
        "") language_label="не определён" ;;
        *) language_label="выбран другой (${language})" ;;
    esac

    if [ -z "$cache_root" ]; then
        gui_emit STATE "missing"
        gui_emit TITLE "Данные игры не найдены"
        gui_emit DETAIL "Установите игру через официальный Mac-лаунчер, запустите её и загрузите украинский язык."
        gui_emit CLIENT "Mac"
        gui_emit VERSION "—"
        gui_emit CACHE "—"
        gui_emit LANGUAGE "$language_label"
        gui_emit RUNNING "$running"
        gui_emit PATCHER "$APP_VERSION"
        gui_emit CAN_INSTALL "no"
        gui_emit CAN_RESTORE "no"
        return 0
    fi

    select_state_file "$cache_root"
    target="${cache_root}/${TARGET_NAME}"
    version="$(cache_display_version "$cache_root")"
    case "$cache_root" in
        "${HOME}/Library/Application Support/hitzone.anima.spirit.guardians/i18n") client="Нативный Mac-клиент" ;;
        "${HOME}/Library/Containers/"*) client="Старый iOS-клиент" ;;
        *) client="Unity-клиент" ;;
    esac

    if [ ! -f "$target" ]; then
        state="needs-language"
        title="Нужно загрузить украинский язык"
        detail="Откройте игру, выберите украинский язык, дождитесь загрузки и полностью закройте игру."
    else
        current="$(sha256_of "$target")"
        original=""; patched=""; saved_english=""
        [ -f "$STATE_FILE" ] && original="$(json_field "$STATE_FILE" original_sha256 || true)"
        [ -f "$STATE_FILE" ] && patched="$(json_field "$STATE_FILE" patched_sha256 || true)"
        [ -f "$STATE_FILE" ] && saved_english="$(json_field "$STATE_FILE" english_sha256 || true)"
        matching_downloaded_tables "$cache_root" || tables_match="no"
        if [ "$tables_match" = "no" ]; then
            state="changed"
            title="Языковые файлы разных версий"
            detail="Заново загрузите английский и украинский языки в игре, затем полностью закройте её."
        elif [ -n "$patched" ] && [ "$current" = "$patched" ] \
             && [ -n "$saved_english" ] && [ "$saved_english" != "$(sha256_of "$cache_root/${ENGLISH_NAME}")" ]; then
            state="changed"
            title="Английская таблица обновилась"
            detail="Перевод относится к прежнему английскому файлу. Обновите также украинский язык в игре и установите перевод заново."
        elif [ -n "$patched" ] && [ "$current" = "$patched" ]; then
            state="russian"
            title="Русский перевод установлен"
            detail="Файл проверен и совпадает с установленной сборкой перевода."
        elif [ -n "$original" ] && [ "$current" = "$original" ]; then
            state="original"
            title="Готово к установке"
            detail="Сейчас используется оригинальный украинский файл. Можно установить русский перевод."
        elif [ -f "$STATE_FILE" ]; then
            state="changed"
            title="Языковой файл обновился"
            detail="Игра перезаписала локализацию после обновления или смены языка. Установите перевод заново."
        else
            state="ready"
            title="Готово к установке"
            detail="Найден актуальный украинский файл. Перед изменением будет создана проверенная резервная копия."
        fi
    fi

    if [ "$running" = "yes" ]; then
        detail="${detail} Игра сейчас запущена — перед установкой её нужно закрыть."
    fi
    if [ -n "$language" ] && [ "$language" != "8" ]; then
        state="needs-language"
        title="В игре выбран не украинский язык"
        detail="Откройте игру, выберите украинский язык, дождитесь загрузки и полностью закройте игру. Иначе русский слот не будет активен."
    fi
    if [ -f "$target" ] && [ "$tables_match" = "yes" ] \
        && { [ -z "$language" ] || [ "$language" = "8" ]; }; then
        can_install="yes"
    fi
    restore_available "$target" && can_restore="yes"
    protection="$(protection_reason "$cache_root")" || protection="Не удалось завершить проверку файлов игры."
    if [ -n "$protection" ]; then
        state="changed"
        title="Установка остановлена: проверка защиты"
        detail="Возможна защита файлов игры. Подробности — в журнале. Закройте игру и восстановите оригинал."
        can_install="no"
        printf 'PROTECTION_CHECK_BLOCKED: %s\n' "$protection" >>"$LOG_FILE"
    fi
    gui_emit STATE "$state"
    gui_emit TITLE "$title"
    gui_emit DETAIL "$detail"
    gui_emit CLIENT "$client"
    gui_emit VERSION "$version"
    gui_emit CACHE "$cache_root"
    gui_emit LANGUAGE "$language_label"
    gui_emit RUNNING "$running"
    gui_emit PATCHER "$APP_VERSION"
    gui_emit CAN_INSTALL "$can_install"
    gui_emit CAN_RESTORE "$can_restore"
}

wait_until_game_closed() {
    local retry
    while game_running; do
        retry="$(ask "Игра сейчас запущена.

Полностью закройте Invokers (Cmd+Q), иначе изменения не сохранятся." "Отмена" "Я закрыл, продолжить")"
        [ "$retry" = "Я закрыл, продолжить" ] || return 1
    done
}

run_gui_mac_action() {
    local action="$1" cache_root
    cache_root="$(find_cache_root || true)"
    if [ -z "$cache_root" ]; then
        diagnose_missing_game
        return 1
    fi
    select_state_file "$cache_root"
    printf 'cache root: %s\n' "$cache_root" >>"$LOG_FILE"
    require_disk_access "${cache_root}/${ENGLISH_NAME}"
    wait_until_game_closed || return 0
    require_cli
    case "$action" in
        install) do_install "$cache_root" ;;
        restore) do_restore "$cache_root" ;;
    esac
}

run_gui_android_action() {
    local serial action
    if ! find_adb; then
        offer_adb_install || return 0
    fi
    serial="$(android_device)"
    if [ -z "$serial" ]; then
        say_error "Планшет или телефон Android не найден.

Подключите устройство, включите отладку по USB и подтвердите доступ на его экране."
        return 1
    fi
    android_has_game "$serial" || { say_error "На подключённом устройстве не установлена Invokers: Titan Legacy."; return 1; }
    require_cli
    action="$(ask "Android-устройство: ${serial}

Что сделать?" "Отмена" "Восстановить" "Установить перевод")"
    case "$action" in
        "Установить перевод") android_install "$serial" ;;
        "Восстановить") android_restore "$serial" ;;
    esac
}

# ---------- main ----------

# Tests source this file to exercise cache selection without opening any dialogs.
if [ "${INVOKERSRU_LIBRARY_MODE:-0}" = "1" ]; then
    return 0 2>/dev/null || exit 0
fi

# A relaunch triggered by the Full Disk Access prompt should land the user back where they were,
# not at the beginning of the same explanation they just read.
RESUMING=false
if [ -f "$RESUME_MARKER" ]; then
    marked="$(cat "$RESUME_MARKER" 2>/dev/null || echo 0)"
    now="$(date +%s)"
    [ $((now - marked)) -lt 600 ] 2>/dev/null && RESUMING=true
    rm -f "$RESUME_MARKER"
fi

COMMAND="${1:-interactive}"
self_update "$@"

if [ "$COMMAND" = "--gui-status" ]; then
    gui_status
    exit 0
fi

case "$COMMAND" in
    --gui-install)
        check_app_update
        run_gui_mac_action install
        exit $?
        ;;
    --gui-restore)
        check_app_update
        run_gui_mac_action restore
        exit $?
        ;;
esac

if [ "$RESUMING" = false ]; then
    choice="$(ask "Неофициальный любительский русификатор Invokers: Titan Legacy.

Приложение не связано с HitZone Inc. Оно изменяет один файл кэша локализации внутри папки данных игры и сохраняет оригинал. При известных локальных признаках защиты установка останавливается. Проверка не обнаруживает все возможные защиты и серверные ограничения и не гарантирует отсутствие банов.

Перевод загружается из каталога проекта и собирается для найденных языковых файлов. Количество переведённых строк будет показано после установки; строки, не прошедшие проверку, остаются оригинальными. Перевод любительский. Используйте на свой риск." "Выход" "Продолжить")"
    [ "$choice" = "Продолжить" ] || exit 0
fi

check_app_update

# Android and iOS are intentionally not exposed in the supported 3.0 release.
# Their experimental transport helpers stay in the source tree for future work.
ANDROID_SERIAL=""

CACHE_ROOT="$(find_cache_root || true)"
PLATFORM="mac"

if [ -z "$CACHE_ROOT" ]; then
    diagnose_missing_game
    CACHE_ROOT="$(find_cache_root || true)"
    [ -n "$CACHE_ROOT" ] || die "Данные игры так и не найдены.

Запустите игру, дождитесь главного меню и попробуйте снова."
fi

if [ "$PLATFORM" = "mac" ]; then
    select_state_file "$CACHE_ROOT"
    printf 'cache root: %s\n' "$CACHE_ROOT" >>"$LOG_FILE"
    require_disk_access "${CACHE_ROOT}/${ENGLISH_NAME}"

    wait_until_game_closed || exit 0

fi

require_cli

action="$(ask "$(describe_state "$CACHE_ROOT")

Что сделать?" "Отмена" "Восстановить оригинал" "Установить перевод")"

case "$action" in
    "Установить перевод") do_install "$CACHE_ROOT" ;;
    "Восстановить оригинал") do_restore "$CACHE_ROOT" ;;
    *) exit 0 ;;
esac
