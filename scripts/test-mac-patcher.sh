#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/invokersru-mac-test.XXXXXX")"
trap 'rm -rf "$TEST_ROOT"' EXIT

export HOME="${TEST_ROOT}/home"
export INVOKERSRU_SUPPORT_DIR="${TEST_ROOT}/support"
export INVOKERSRU_LIBRARY_MODE=1

NATIVE="${HOME}/Library/Application Support/hitzone.anima.spirit.guardians/i18n"
OLD="${HOME}/Library/Containers/OLD/Data/Documents/i18n"
EXPLICIT="${HOME}/chosen/i18n"
mkdir -p "$NATIVE" "$OLD" "$EXPLICIT"
touch "$NATIVE/dl_en_US.bin" "$OLD/dl_en_US.bin" "$EXPLICIT/dl_en_US.bin"
printf '0.60.1289' >"$NATIVE/dl_en_US.bin.ver"
printf '0.60.1247' >"$OLD/dl_en_US.bin.ver"
printf '0.60.1000' >"$EXPLICIT/dl_en_US.bin.ver"

# shellcheck source=../mac/patcher-main.sh
source "${REPO_ROOT}/mac/patcher-main.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }

selected="$(find_cache_root)"
[ "$selected" = "$NATIVE" ] || fail "new native cache was not preferred: $selected"

printf '0.60.1300' >"$OLD/dl_en_US.bin.ver"
selected="$(find_cache_root)"
[ "$selected" = "$OLD" ] || fail "newest cache version was not selected: $selected"

export INVOKERSRU_CACHE_ROOT="$EXPLICIT"
selected="$(find_cache_root)"
[ "$selected" = "$EXPLICIT" ] || fail "explicit cache root was ignored: $selected"
unset INVOKERSRU_CACHE_ROOT

select_state_file "$NATIVE"
native_state="$STATE_FILE"
select_state_file "$OLD"
old_state="$STATE_FILE"
[ "$native_state" != "$old_state" ] || fail "different clients share one state file"

printf 'PASS: mac cache selection and state isolation\n'

# The protection check must stop real game markers while permitting newer content and normal
# macOS code signing. Fixtures stay outside the real user's game/cache and never launch a client.
TEST_BUNDLE="${TEST_ROOT}/Invokers.app"
mkdir -p "${TEST_BUNDLE}/Contents/_CodeSignature" "${TEST_BUNDLE}/Contents/Frameworks"
touch "${TEST_BUNDLE}/Contents/_CodeSignature/CodeResources"
touch "${NATIVE}/dl_uk_UA.bin" "${NATIVE}/dl_uk_UA.bin.src"
printf '99.999.9999' >"${NATIVE}/dl_uk_UA.bin.ver"
game_bundle() { printf '%s\n' "$TEST_BUNDLE"; }
game_running() { return 1; }
preferred_language() { printf '8'; }
say_error() { printf '%s\n' "$1" >"${TEST_ROOT}/error.txt"; }
say_info() { :; }

[ -z "$(protection_reason "$NATIVE")" ] || fail 'new version or ordinary code signing blocked install'
mkdir -p "${TEST_ROOT}/OtherGame.app/EasyAntiCheat"
[ -z "$(protection_reason "$NATIVE")" ] || fail 'unrelated game protection blocked install'
touch "${TEST_BUNDLE}/Contents/EasyAntiCheat_readme.txt"
[ -z "$(protection_reason "$NATIVE")" ] || fail 'a readme was mistaken for anti-cheat'
mkdir -p "${TEST_BUNDLE}/Contents/Frameworks/EasyAntiCheat_EOS"
reason="$(protection_reason "$NATIVE")"
[[ "$reason" == *EasyAntiCheat_EOS* ]] || fail 'game anti-cheat directory was not detected'
if require_patch_preflight "$NATIVE"; then fail 'anti-cheat preflight was allowed'; fi
rmdir "${TEST_BUNDLE}/Contents/Frameworks/EasyAntiCheat_EOS"
touch "${TEST_BUNDLE}/Contents/Frameworks/libEasyAntiCheat.dylib"
[[ "$(protection_reason "$NATIVE")" == *libEasyAntiCheat.dylib* ]] || fail 'native anti-cheat library was not detected'
rm "${TEST_BUNDLE}/Contents/Frameworks/libEasyAntiCheat.dylib"
find() { command find "$@"; return 1; }
[ -n "$(protection_reason "$NATIVE")" ] || fail 'incomplete scan was treated as clear'
unset -f find

for sidecar in dl_uk_UA.bin.sig dl_en_US.bin.br.sha256 uk_UA.bin.signature \
               dl_uk_UA.bin.ver.sig en_US.bin.src.sha512 \
               i18n.manifest.json i18n.manifest.sig i18n.manifest.json.sig \
               localization.manifest.json.sha256 i18n.signatures.json localization.signatures.json; do
    touch "${NATIVE}/${sidecar}"
    reason="$(protection_reason "$NATIVE")"
    [[ "$reason" == *"$sidecar"* ]] || fail "localization sidecar not detected: $sidecar"
    rm "${NATIVE}/${sidecar}"
done
ln -s "${TEST_ROOT}/missing-table" "${EXPLICIT}/dl_uk_UA.bin"
[ -n "$(protection_reason "$EXPLICIT")" ] || fail 'linked localization target was accepted'

# Simulate a launcher adding a signature while the patcher builds the translation. It must stop
# before atomic_install, even though the first preflight passed.
select_state_file "$NATIVE"
printf 'original-language' >"${NATIVE}/dl_uk_UA.bin"
original_hash="$(sha256_of "${NATIVE}/dl_uk_UA.bin")"
refresh_overlay() { return 0; }
fake_cli() {
    printf 'new-translation' >"${WORK_DIR}/${TARGET_NAME}.ru"
    printf '{"applied_ru":1}\n' >"${WORK_DIR}/report.json"
    touch "${NATIVE}/dl_uk_UA.bin.sig"
}
CLI=fake_cli
atomic_install() { printf 'unexpected write\n' >"${TEST_ROOT}/wrote.txt"; return 0; }
if do_install "$NATIVE"; then fail 'new protection added during build did not stop install'; fi
[ ! -f "${TEST_ROOT}/wrote.txt" ] || fail 'protected target reached atomic_install'
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" = "$original_hash" ] || fail 'protected target changed'
grep -q PROTECTION_CHECK_BLOCKED "$LOG_FILE" || fail 'block reason not logged'

# Protection blocks installation in the native UI but must keep original restoration available.
backup="${BACKUP_DIR}/${original_hash}.${TARGET_NAME}"
printf '{"original_sha256":"%s",\n"backup_path":"%s"}\n' "$original_hash" "$backup" >"$STATE_FILE"
export INVOKERSRU_CACHE_ROOT="$NATIVE"
status="$(gui_status)"
[[ "$status" == *'CAN_INSTALL=no'* && "$status" == *'CAN_RESTORE=yes'* ]] || fail 'GUI gate blocked restoration or allowed installation'
printf 'previous-translation' >"${NATIVE}/dl_uk_UA.bin"
atomic_install() { cp "$1" "$2"; }
do_restore "$NATIVE" || fail 'restore blocked by protection marker'
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" = "$original_hash" ] || fail 'restore did not return original'
printf 'previous-translation' >"${NATIVE}/dl_uk_UA.bin"
game_running() { return 0; }
if do_restore "$NATIVE"; then fail 'restore permitted while game running'; fi
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" != "$original_hash" ] || fail 'running game target changed'

printf 'PASS: mac protection markers, write-time recheck and original restoration\n'
