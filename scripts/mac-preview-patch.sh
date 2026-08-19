#!/usr/bin/env bash
# Applies a locally built Russian preview to the macOS (iOS-on-Mac) localization cache.
#
# This is a preview tool for testing a build on your own machine, not the supervised Windows
# installer. It only ever touches one file inside the app's writable container:
#     ~/Library/Containers/<UUID>/Data/Documents/i18n/dl_uk_UA.bin
# The signed application bundle is never modified, so the code signature and FairPlay stay intact.
#
# Usage:
#   mac-preview-patch.sh status
#   mac-preview-patch.sh apply <patched-dl_uk_UA.bin>
#   mac-preview-patch.sh restore
#
# Options:
#   --cache-root PATH   Use an explicit i18n directory instead of auto-discovery.

set -euo pipefail

TARGET_NAME="dl_uk_UA.bin"
STAMP_NAME="dl_uk_UA.bin.ver"
STATE_DIR="${HOME}/Library/Application Support/InvokersRu/mac-preview"
STATE_FILE="${STATE_DIR}/state.json"
BACKUP_DIR="${STATE_DIR}/backups"

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
note() { printf '%s\n' "$*"; }

sha256_of() { shasum -a 256 "$1" | awk '{print toupper($1)}'; }

json_field() {
    # json_field <file> <key>  — reads a flat "key": "value" pair without needing jq.
    [ -f "$1" ] || return 1
    /usr/bin/sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$1" | head -1
}

discover_cache_root() {
    local containers="${HOME}/Library/Containers" found=() candidate
    [ -d "$containers" ] || die "No application container directory at ${containers}; pass --cache-root PATH."
    for container in "$containers"/*/; do
        candidate="${container}Data/Documents/i18n"
        [ -f "${candidate}/dl_en_US.bin" ] && found+=("$candidate")
    done
    case "${#found[@]}" in
        1) printf '%s\n' "${found[0]}" ;;
        0) die "No game localization cache containing dl_en_US.bin was found under ${containers}; pass --cache-root PATH." ;;
        *) die "Several containers under ${containers} hold dl_en_US.bin; refusing to guess, pass --cache-root PATH." ;;
    esac
}

require_game_stopped() {
    if pgrep -f "Invokers.app/Invokers" >/dev/null 2>&1; then
        die "Invokers is running. Quit the game completely, then run this again."
    fi
}

# Writes $2 over $1 atomically, keeping the replacement on the same filesystem.
atomic_install() {
    local source="$1" target="$2" directory temp
    directory="$(dirname "$target")"
    temp="$(mktemp "${directory}/.${TARGET_NAME}.invokersru.XXXXXX")"
    trap 'rm -f "$temp"' RETURN
    cat "$source" > "$temp"
    sync
    [ "$(sha256_of "$temp")" = "$(sha256_of "$source")" ] || die "Staged file does not match its source; nothing was replaced."
    chmod 644 "$temp"
    mv -f "$temp" "$target"
    trap - RETURN
}

CACHE_ROOT=""
ACTION="${1:-}"
[ -n "$ACTION" ] || die "Usage: $(basename "$0") status|apply <file>|restore [--cache-root PATH]"
shift || true

PATCHED_FILE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --cache-root) CACHE_ROOT="${2:-}"; [ -n "$CACHE_ROOT" ] || die "--cache-root needs a value."; shift 2 ;;
        -*) die "Unknown option: $1" ;;
        *) [ -z "$PATCHED_FILE" ] || die "Unexpected extra argument: $1"; PATCHED_FILE="$1"; shift ;;
    esac
done

[ -n "$CACHE_ROOT" ] || CACHE_ROOT="$(discover_cache_root)"
[ -d "$CACHE_ROOT" ] || die "Cache root is not a directory: ${CACHE_ROOT}"
TARGET="${CACHE_ROOT}/${TARGET_NAME}"
STAMP="${CACHE_ROOT}/${STAMP_NAME}"

case "$ACTION" in
status)
    note "Cache root:   ${CACHE_ROOT}"
    if [ ! -f "$TARGET" ]; then
        note "Target:       ${TARGET_NAME} is MISSING — select Ukrainian in the game once so the client downloads it."
        exit 5
    fi
    current="$(sha256_of "$TARGET")"
    note "Target:       ${TARGET_NAME}"
    note "Current SHA:  ${current}"
    [ -f "$STAMP" ] && note "Version:      $(cat "$STAMP")"
    if [ -f "$STATE_FILE" ]; then
        original="$(json_field "$STATE_FILE" original_sha256 || true)"
        patched="$(json_field "$STATE_FILE" patched_sha256 || true)"
        note "Backup SHA:   ${original}"
        if [ "$current" = "$patched" ]; then
            note "State:        PATCHED by this tool"
        elif [ "$current" = "$original" ]; then
            note "State:        original restored"
        else
            note "State:        file changed outside this tool (the client probably re-downloaded it)"
        fi
    else
        note "State:        never patched by this tool"
    fi
    ;;

apply)
    [ -n "$PATCHED_FILE" ] || die "Usage: $(basename "$0") apply <patched-dl_uk_UA.bin>"
    [ -f "$PATCHED_FILE" ] || die "Patched file not found: ${PATCHED_FILE}"
    [ -f "$TARGET" ] || die "${TARGET_NAME} is missing. Select Ukrainian in the game once so the client downloads it."
    require_game_stopped

    current="$(sha256_of "$TARGET")"
    incoming="$(sha256_of "$PATCHED_FILE")"
    [ "$current" != "$incoming" ] || { note "Target already holds exactly this build; nothing to do."; exit 0; }

    mkdir -p "$BACKUP_DIR"
    known_patched=""
    [ -f "$STATE_FILE" ] && known_patched="$(json_field "$STATE_FILE" patched_sha256 || true)"

    if [ -n "$known_patched" ] && [ "$current" = "$known_patched" ]; then
        # Re-applying over our own previous preview: the recorded backup is still the true original.
        original="$(json_field "$STATE_FILE" original_sha256)"
        backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
        [ -f "$backup" ] || die "Recorded backup is missing: ${backup}. Restore manually before re-applying."
        [ "$(sha256_of "$backup")" = "$original" ] || die "Recorded backup is corrupt; refusing to continue."
        note "Re-applying over a previous preview; keeping the existing backup."
    else
        original="$current"
        backup="${BACKUP_DIR}/${original}.${TARGET_NAME}"
        if [ -f "$backup" ] && [ "$(sha256_of "$backup")" = "$original" ]; then
            note "Backup already present and verified."
        else
            cat "$TARGET" > "${backup}.tmp"
            sync
            [ "$(sha256_of "${backup}.tmp")" = "$original" ] || { rm -f "${backup}.tmp"; die "Backup copy did not verify; nothing was changed."; }
            mv -f "${backup}.tmp" "$backup"
            note "Backup written: ${backup}"
        fi
    fi

    atomic_install "$PATCHED_FILE" "$TARGET"
    final="$(sha256_of "$TARGET")"
    [ "$final" = "$incoming" ] || die "Installed file does not match the requested build; restore from ${backup}."

    mkdir -p "$STATE_DIR"
    cat > "$STATE_FILE" <<JSON
{
  "schema": 1,
  "cache_root": "${CACHE_ROOT}",
  "target": "${TARGET_NAME}",
  "original_sha256": "${original}",
  "patched_sha256": "${final}",
  "backup_path": "${backup}"
}
JSON
    note "Applied. Original SHA ${original} -> preview SHA ${final}"
    note "Restore any time with: $(basename "$0") restore"
    ;;

restore)
    [ -f "$STATE_FILE" ] || die "No state file at ${STATE_FILE}; this tool has not patched anything."
    require_game_stopped
    original="$(json_field "$STATE_FILE" original_sha256)"
    backup="$(json_field "$STATE_FILE" backup_path)"
    [ -f "$backup" ] || die "Backup is missing: ${backup}"
    [ "$(sha256_of "$backup")" = "$original" ] || die "Backup does not match its recorded hash; refusing to restore a corrupt file."

    if [ -f "$TARGET" ] && [ "$(sha256_of "$TARGET")" = "$original" ]; then
        note "Target already holds the original file; nothing to do."
        exit 0
    fi

    atomic_install "$backup" "$TARGET"
    [ "$(sha256_of "$TARGET")" = "$original" ] || die "Restored file did not verify; the backup is still at ${backup}."
    note "Restored the original ${TARGET_NAME} (SHA ${original})."
    ;;

*)
    die "Unknown action: ${ACTION}. Use status, apply <file>, or restore."
    ;;
esac
