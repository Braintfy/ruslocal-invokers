#!/usr/bin/env bash
# Local, source-built macOS installer for an exact community catalog/profile.
# It touches only the existing uk_UA runtime-cache slot and never the signed game bundle.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLI="${ROOT}/InvokersRu.Cli"
CATALOG="${ROOT}/translations/ru_RU.jsonl"
PINS="${ROOT}/community-pins.txt"
CONFIG="${ROOT}/language-config.json"
PROFILE="${ROOT}/profiles/certified-runtime-profile.json"
BUILD_RECEIPT="${ROOT}/profiles/community-build-receipt.json"
TARGET_NAME="dl_uk_UA.bin"
ENGLISH_NAME="dl_en_US.bin"
STAMP_NAME="dl_uk_UA.bin.ver"
TEMP_DIR_TO_CLEAN=""
BACKUP_TMP_TO_CLEAN=""
RESTORE_TMP_TO_CLEAN=""
LOCK_DIR_TO_CLEAN=""

cleanup() {
    if [ -n "$RESTORE_TMP_TO_CLEAN" ]; then rm -f -- "$RESTORE_TMP_TO_CLEAN"; fi
    if [ -n "$BACKUP_TMP_TO_CLEAN" ]; then rm -f -- "$BACKUP_TMP_TO_CLEAN"; fi
    if [ -n "$TEMP_DIR_TO_CLEAN" ]; then rm -rf -- "$TEMP_DIR_TO_CLEAN"; fi
    if [ -n "$LOCK_DIR_TO_CLEAN" ]; then rmdir -- "$LOCK_DIR_TO_CLEAN" 2>/dev/null || true; fi
}
trap cleanup EXIT

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
sha256_of() { shasum -a 256 "$1" | awk '{print toupper($1)}'; }
pin() { /usr/bin/sed -n "s/^$1=//p" "$PINS" | head -1; }
reject_symlink_components() {
    local current="$1" parent
    while [ "$current" != / ] && [ -n "$current" ]; do
        [ ! -L "$current" ] || die "A path component is a symlink: ${current}"
        parent="$(dirname "$current")"
        [ "$parent" != "$current" ] || break
        current="$parent"
    done
}

[ -x "$CLI" ] || die "Packaged CLI is missing or not executable: ${CLI}"
[ -f "$CATALOG" ] && [ ! -L "$CATALOG" ] || die "Catalog is missing or is a symlink."
[ -f "$PINS" ] && [ ! -L "$PINS" ] || die "Pin file is missing or is a symlink."
[ -f "$CONFIG" ] && [ ! -L "$CONFIG" ] || die "Language config is missing or is a symlink."
[ -f "$PROFILE" ] && [ ! -L "$PROFILE" ] || die "Certified profile is missing or is a symlink."
[ -f "$BUILD_RECEIPT" ] && [ ! -L "$BUILD_RECEIPT" ] || die "Exact build receipt is missing or is a symlink."

PACK_ID="$(pin pack_id)"
TARGET_BCP47="$(pin target_bcp47)"
POLICY="$(pin catalog_policy)"
ALLOW_PER_LOCALE="$(pin allow_per_locale_content_version)"
ENGLISH_SHA="$(pin english_sha256)"
BASE_SHA="$(pin base_sha256)"
STAMP_SHA="$(pin stamp_sha256)"
CATALOG_SHA="$(pin catalog_sha256)"
OUTPUT_SHA="$(pin expected_output_sha256)"
CONFIG_SHA="$(pin language_config_sha256)"
PROFILE_SHA="$(pin profile_sha256)"
BUILD_RECEIPT_SHA="$(pin source_build_receipt_sha256)"
[[ "$PACK_ID" =~ ^[a-z0-9][a-z0-9._-]{1,63}$ ]] || die "Unsafe pack id in pin file."
[[ "$TARGET_BCP47" =~ ^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$ ]] || die "Unsafe BCP 47 tag in pin file."
[[ "$POLICY" == "preview-drafts" || "$POLICY" == "release-approved" ]] || die "Unsupported catalog policy."
[[ "$ALLOW_PER_LOCALE" == "true" || "$ALLOW_PER_LOCALE" == "false" ]] || die "Invalid content-version policy."
for digest in "$ENGLISH_SHA" "$BASE_SHA" "$STAMP_SHA" "$CATALOG_SHA" "$OUTPUT_SHA" "$CONFIG_SHA" "$PROFILE_SHA" "$BUILD_RECEIPT_SHA"; do
    [[ "$digest" =~ ^[A-F0-9]{64}$ ]] || die "Invalid SHA-256 pin."
done
[ "$(sha256_of "$CATALOG")" = "$CATALOG_SHA" ] || die "Packaged catalog hash does not match the profile."
[ "$(sha256_of "$CONFIG")" = "$CONFIG_SHA" ] || die "Packaged language config does not match the exact build receipt."
[ "$(sha256_of "$PROFILE")" = "$PROFILE_SHA" ] || die "Packaged profile does not match the exact build receipt."
[ "$(sha256_of "$BUILD_RECEIPT")" = "$BUILD_RECEIPT_SHA" ] || die "Packaged exact build receipt does not match the package pins."

discover_cache_root() {
    local containers="${HOME}/Library/Containers" candidate
    local -a found=()
    [ -d "$containers" ] || die "No application containers directory. Grant Terminal Full Disk Access or install and run the game first."
    for candidate in "$containers"/*/Data/Documents/i18n; do
        [ -f "${candidate}/${ENGLISH_NAME}" ] && found+=("$candidate")
    done
    case "${#found[@]}" in
        1) printf '%s\n' "${found[0]}" ;;
        0) die "No Invokers localization cache was found. Run the game and download the Ukrainian language first." ;;
        *) die "Several localization caches were found; refusing to guess." ;;
    esac
}

require_regular_tuple() {
    local path
    for path in "$ENGLISH" "$TARGET" "$STAMP"; do
        [ -f "$path" ] && [ ! -L "$path" ] || die "Required cache member is missing or is a symlink: ${path}"
    done
    reject_symlink_components "$CACHE_ROOT"
}

require_game_stopped() {
    if pgrep -f 'Invokers.app/Invokers|InvokersLauncher|Invokers Launcher' >/dev/null 2>&1; then
        die "Invokers or its launcher is running. Quit both completely and try again."
    fi
}

verify_static_tuple() {
    [ "$(sha256_of "$ENGLISH")" = "$ENGLISH_SHA" ] || die "English cache does not match the exact certified game profile."
    [ "$(sha256_of "$STAMP")" = "$STAMP_SHA" ] || die "Game version stamp does not match the exact certified profile."
}

CACHE_ROOT="$(discover_cache_root)"
ENGLISH="${CACHE_ROOT}/${ENGLISH_NAME}"
TARGET="${CACHE_ROOT}/${TARGET_NAME}"
STAMP="${CACHE_ROOT}/${STAMP_NAME}"
STATE_ROOT="${HOME}/Library/Application Support/InvokersCommunityLocalization/${PACK_ID}"
BACKUP_ROOT="${STATE_ROOT}/backups"
BACKUP="${BACKUP_ROOT}/${BASE_SHA}.${TARGET_NAME}"
ACTION="${1:-menu}"
require_regular_tuple

acquire_operation_lock() {
    local lock_path
    umask 077
    reject_symlink_components "$STATE_ROOT"
    mkdir -p "$BACKUP_ROOT"
    reject_symlink_components "$BACKUP_ROOT"
    [ -d "$STATE_ROOT" ] && [ ! -L "$STATE_ROOT" ] || die "State root is not a regular directory."
    [ -d "$BACKUP_ROOT" ] && [ ! -L "$BACKUP_ROOT" ] || die "Backup root is not a regular directory."
    chmod 700 "$STATE_ROOT" "$BACKUP_ROOT"
    lock_path="${STATE_ROOT}/operation.lock"
    [ ! -e "$lock_path" ] && [ ! -L "$lock_path" ] \
        || die "Another community patcher process may still be active: ${lock_path}"
    mkdir "$lock_path" 2>/dev/null \
        || die "Another community patcher process is already working with this language pack."
    LOCK_DIR_TO_CLEAN="$lock_path"
    reject_symlink_components "$LOCK_DIR_TO_CLEAN"
}

show_status() {
    local current
    verify_static_tuple
    current="$(sha256_of "$TARGET")"
    printf 'Target language: %s\n' "$TARGET_BCP47"
    printf 'Injection slot:  uk_UA\n'
    printf 'Cache root:      %s\n' "$CACHE_ROOT"
    printf 'Current SHA-256: %s\n' "$current"
    if [ "$current" = "$BASE_SHA" ]; then
        printf 'State:           exact original; ready to apply\n'
    elif [ "$current" = "$OUTPUT_SHA" ]; then
        printf 'State:           exact community localization installed\n'
    else
        printf 'State:           unknown file; apply and restore are blocked\n'
        return 5
    fi
}

apply_localization() {
    local current temp_dir built report
    require_game_stopped
    verify_static_tuple
    current="$(sha256_of "$TARGET")"
    [ "$current" != "$OUTPUT_SHA" ] || { printf 'Exact localization is already installed.\n'; return 0; }
    [ "$current" = "$BASE_SHA" ] || die "Target is neither the exact original nor the exact built output."
    acquire_operation_lock
    require_game_stopped
    [ "$(sha256_of "$TARGET")" = "$BASE_SHA" ] || die "Target changed before the operation lock was acquired."

    temp_dir="$(mktemp -d "${CACHE_ROOT}/.community-${PACK_ID}.XXXXXX")"
    TEMP_DIR_TO_CLEAN="$temp_dir"
    reject_symlink_components "$temp_dir"
    built="${temp_dir}/${TARGET_NAME}"
    report="${temp_dir}/build-report.json"
    local -a build_args=(build --english "$ENGLISH" --base "$TARGET" --translations "$CATALOG" --output "$built" --report "$report" --raw)
    if [ "$POLICY" = "preview-drafts" ]; then build_args+=(--include-draft); else build_args+=(--release); fi
    if [ "$ALLOW_PER_LOCALE" = "true" ]; then build_args+=(--per-locale-content-version); fi
    "$CLI" "${build_args[@]}"
    [ -f "$built" ] && [ ! -L "$built" ] || die "CLI did not produce a regular LOC1 output."
    [ "$(sha256_of "$built")" = "$OUTPUT_SHA" ] || die "Locally composed output does not match the exact certified hash."

    if [ -f "$BACKUP" ]; then
        [ ! -L "$BACKUP" ] && [ "$(sha256_of "$BACKUP")" = "$BASE_SHA" ] || die "Existing backup is invalid."
    else
        [ ! -e "$BACKUP" ] && [ ! -L "$BACKUP" ] || die "Backup path exists but is not a regular verified backup."
        BACKUP_TMP_TO_CLEAN="$(mktemp "${BACKUP}.tmp.XXXXXX")"
        [ -f "$BACKUP_TMP_TO_CLEAN" ] && [ ! -L "$BACKUP_TMP_TO_CLEAN" ] || die "Could not create a regular backup staging file."
        cp -p "$TARGET" "$BACKUP_TMP_TO_CLEAN"
        sync
        [ "$(sha256_of "$BACKUP_TMP_TO_CLEAN")" = "$BASE_SHA" ] || die "Backup verification failed."
        chmod 600 "$BACKUP_TMP_TO_CLEAN"
        [ ! -e "$BACKUP" ] && [ ! -L "$BACKUP" ] || die "Backup destination appeared during staging."
        ln "$BACKUP_TMP_TO_CLEAN" "$BACKUP" || die "Could not create the immutable backup without overwriting an existing path."
        rm -f -- "$BACKUP_TMP_TO_CLEAN"
        BACKUP_TMP_TO_CLEAN=""
        [ -f "$BACKUP" ] && [ ! -L "$BACKUP" ] && [ "$(sha256_of "$BACKUP")" = "$BASE_SHA" ] \
            || die "Created backup failed final verification."
    fi

    require_game_stopped
    verify_static_tuple
    [ "$(sha256_of "$TARGET")" = "$BASE_SHA" ] || die "Target changed while the output was being prepared."
    chmod 644 "$built"
    sync
    mv "$built" "$TARGET"
    [ "$(sha256_of "$TARGET")" = "$OUTPUT_SHA" ] || die "Atomic replacement did not produce the exact expected output; backup remains at ${BACKUP}."
    printf 'Installed exact %s localization. Backup: %s\n' "$TARGET_BCP47" "$BACKUP"
    rm -rf -- "$temp_dir"
    TEMP_DIR_TO_CLEAN=""
}

restore_original() {
    local current restore_tmp
    require_game_stopped
    verify_static_tuple
    acquire_operation_lock
    [ -f "$BACKUP" ] && [ ! -L "$BACKUP" ] || die "Exact backup is missing: ${BACKUP}"
    [ "$(sha256_of "$BACKUP")" = "$BASE_SHA" ] || die "Backup hash does not match the exact original."
    current="$(sha256_of "$TARGET")"
    [ "$current" != "$BASE_SHA" ] || { printf 'Exact original is already restored.\n'; return 0; }
    [ "$current" = "$OUTPUT_SHA" ] || die "Target changed outside this tool; refusing to overwrite it."
    restore_tmp="$(mktemp "${CACHE_ROOT}/.${TARGET_NAME}.restore-${PACK_ID}.XXXXXX")"
    RESTORE_TMP_TO_CLEAN="$restore_tmp"
    [ -f "$restore_tmp" ] && [ ! -L "$restore_tmp" ] || die "Could not create a regular restore staging file."
    cp -p "$BACKUP" "$restore_tmp"
    sync
    [ "$(sha256_of "$restore_tmp")" = "$BASE_SHA" ] || { rm -f "$restore_tmp"; die "Restore staging verification failed."; }
    require_game_stopped
    verify_static_tuple
    [ "$(sha256_of "$TARGET")" = "$OUTPUT_SHA" ] || { rm -f "$restore_tmp"; die "Target changed before restore."; }
    mv "$restore_tmp" "$TARGET"
    RESTORE_TMP_TO_CLEAN=""
    [ "$(sha256_of "$TARGET")" = "$BASE_SHA" ] || die "Restored file hash is not the exact original."
    printf 'Restored the exact original uk_UA cache.\n'
}

case "$ACTION" in
    status) show_status ;;
    apply) apply_localization ;;
    restore) restore_original ;;
    menu)
        printf 'Community localization %s\n' "$TARGET_BCP47"
        printf 'Before applying: select Ukrainian in the game, wait for the download, then fully quit the game.\n'
        printf '1. Check status\n2. Install localization\n3. Restore original\n0. Exit\n'
        read -r -p 'Choose: ' choice
        case "$choice" in 1) show_status ;; 2) read -r -p 'Type APPLY: ' answer; [ "$answer" = APPLY ] || die 'Cancelled.'; apply_localization ;; 3) read -r -p 'Type RESTORE: ' answer; [ "$answer" = RESTORE ] || die 'Cancelled.'; restore_original ;; 0) exit 0 ;; *) die 'Unknown choice.' ;; esac
        ;;
    *) die "Usage: $(basename "$0") [menu|status|apply|restore]" ;;
esac
