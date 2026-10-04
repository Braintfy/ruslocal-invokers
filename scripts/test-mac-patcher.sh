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

fail() { printf 'FAIL: %s\n' "$1"; exit 1; }
trap 'test_status=$?; printf "FAIL: command at line %s exited %s\n" "$LINENO" "$test_status"; exit "$test_status"' ERR

selected="$(find_cache_root)"
[ "$selected" = "$NATIVE" ] || fail "new native cache was not preferred: $selected"

printf '0.60.1300' >"$OLD/dl_en_US.bin.ver"
selected="$(find_cache_root)"
[ "$selected" = "$NATIVE" ] || fail "legacy iOS cache displaced the native Mac client: $selected"

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

(
    # The new downloaded LOC1 identity takes precedence over a stale legacy stamp.
    inspect_field() { printf '%s' "$test_family"; }
    test_family=0.61.0
    [ "$(cache_version "$NATIVE")" = '0.61.0' ] || fail 'versioned downloaded identity was ignored'
    test_family=00.61.0
    [ "$(cache_version "$NATIVE")" = '0.60.1289' ] || fail 'noncanonical identity was accepted'
    test_family=ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa
    [ "$(cache_version "$NATIVE")" = '0.60.1289' ] || fail 'legacy version stamp was ignored'
)
printf 'PASS: mac versioned downloaded identity and legacy stamps\n'

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
marker="${NATIVE}/dl_uk_UA.bin.sig"
[ ! -e "$marker" ] || fail 'write-time marker existed before the first preflight'
blocked_before="$(grep -c PROTECTION_CHECK_BLOCKED "$LOG_FILE" || true)"
if (
    # Only this fixture bypasses LOC1 parsing. The marker is added by the build, so the first
    # preflight must pass and the second one must reject it before atomic_install.
    matching_downloaded_tables() { return 0; }
    refresh_overlay() { return 0; }
    fake_cli() {
        printf 'new-translation' >"${WORK_DIR}/${TARGET_NAME}.ru"
        printf '{"applied_ru":1}\n' >"${WORK_DIR}/report.json"
        touch "$marker"
    }
    CLI=fake_cli
    atomic_install() { printf 'unexpected write\n' >"${TEST_ROOT}/wrote.txt"; return 0; }
    do_install "$NATIVE"
); then fail 'new protection added during build did not stop install'; fi
[ -f "$marker" ] || fail 'build was never reached before protection rejection'
blocked_after="$(grep -c PROTECTION_CHECK_BLOCKED "$LOG_FILE" || true)"
[ "$blocked_after" -eq $((blocked_before + 1)) ] || fail 'second preflight did not record a fresh protection block'
grep -Fq "PROTECTION_CHECK_BLOCKED: Обнаружен файл проверки локализации: ${marker}" "$LOG_FILE" \
    || fail 'second preflight blocked for a reason other than the new localization marker'
[ ! -f "${TEST_ROOT}/wrote.txt" ] || fail 'protected target reached atomic_install'
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" = "$original_hash" ] || fail 'protected target changed'

# Protection blocks installation in the native UI but must keep restoration of our own patch available.
backup="${BACKUP_DIR}/${original_hash}.${TARGET_NAME}"
printf 'original-language' >"$backup"
printf 'previous-translation' >"${NATIVE}/dl_uk_UA.bin"
patched_hash="$(sha256_of "${NATIVE}/dl_uk_UA.bin")"
printf '{"cache_root":"%s",\n"original_sha256":"%s",\n"patched_sha256":"%s",\n"backup_path":"%s"}\n' \
    "$NATIVE" "$original_hash" "$patched_hash" "$backup" >"$STATE_FILE"
export INVOKERSRU_CACHE_ROOT="$NATIVE"
status="$(gui_status)"
[[ "$status" == *'CAN_INSTALL=no'* && "$status" == *'CAN_RESTORE=yes'* ]] || fail 'GUI gate blocked restoration or allowed installation'
atomic_install() { cp "$1" "$2"; }
do_restore "$NATIVE" || fail 'restore blocked by protection marker'
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" = "$original_hash" ] || fail 'restore did not return original'

# The already restored original needs no further write.
atomic_install() { printf 'unexpected write\n' >"${TEST_ROOT}/wrote.txt"; return 1; }
do_restore "$NATIVE" || fail 'restore rejected the already original file'
[ ! -f "${TEST_ROOT}/wrote.txt" ] || fail 'restore rewrote the already original file'

# After a game update, a different current file is not the patcher-owned B and must never be
# overwritten with old backup A. The UI must not offer that restore operation either.
atomic_install() { cp "$1" "$2"; }
printf 'official-after-update' >"${NATIVE}/dl_uk_UA.bin"
updated_hash="$(sha256_of "${NATIVE}/dl_uk_UA.bin")"
if do_restore "$NATIVE"; then fail 'restore accepted a newer official file'; fi
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" = "$updated_hash" ] || fail 'restore overwrote a newer official file'
status="$(gui_status)"
[[ "$status" == *'CAN_RESTORE=no'* ]] || fail 'GUI offered restoration over a newer official file'

printf 'previous-translation' >"${NATIVE}/dl_uk_UA.bin"
game_running() { return 0; }
if do_restore "$NATIVE"; then fail 'restore permitted while game running'; fi
[ "$(sha256_of "${NATIVE}/dl_uk_UA.bin")" = "$patched_hash" ] || fail 'running game target changed'

printf 'PASS: mac protection markers, write-time recheck and original restoration\n'

# A byte-for-byte match with the previously patched UK file alone is not enough to call it current:
# the English source may have changed while the game left that UK file in place. Simulate LOC1
# inspection independently of the fixture bytes, so both family and Prod revision can be tested.
(
    rm -f "${NATIVE}/dl_uk_UA.bin.sig"
    game_running() { return 1; }
    inspect_field() {
        local file="$1" field="$2"
        case "$field" in
            content_guid)
                [ "$file" = "${NATIVE}/${ENGLISH_NAME}" ] && printf '%s' "$english_family" || printf '%s' "$ukrainian_family"
                ;;
            content_version)
                [ "$file" = "${NATIVE}/${ENGLISH_NAME}" ] && printf '%s' "$english_revision" || printf '%s' "$ukrainian_revision"
                ;;
        esac
    }
    english_family=0.61.1
    ukrainian_family=0.61.1
    english_revision=Prod_0.61.1_3
    ukrainian_revision=Prod_0.61.1_5
    matching_downloaded_tables "$NATIVE" || fail 'same-family EN revision 3 and UK revision 5 were rejected'

    ukrainian_family=0.61.2
    ukrainian_revision=Prod_0.61.2_5
    if matching_downloaded_tables "$NATIVE"; then fail 'different downloaded LOC1 families were accepted'; fi
    ukrainian_family=0.61.1
    if matching_downloaded_tables "$NATIVE"; then fail 'Prod revision from another family was accepted'; fi
    ukrainian_revision=Prod_0.61.1_beta
    if matching_downloaded_tables "$NATIVE"; then fail 'malformed Prod revision was accepted'; fi
    ukrainian_revision=Prod_0.61.1_5

    # Legacy UUID families may carry locale-specific release revisions.
    english_family=ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa
    ukrainian_family="$english_family"
    english_revision=Prod_0.60.1289_81
    ukrainian_revision=Prod_0.60.1289_82
    matching_downloaded_tables "$NATIVE" || fail 'legacy UUID family with locale-specific revisions was rejected'
    english_family=0.61.1
    ukrainian_family=0.61.1
    english_revision=Prod_0.61.1_3
    ukrainian_revision=Prod_0.61.1_5
    printf 'english-before-update' >"${NATIVE}/${ENGLISH_NAME}"
    english_hash="$(sha256_of "${NATIVE}/${ENGLISH_NAME}")"
    printf 'previous-translation' >"${NATIVE}/${TARGET_NAME}"
    select_state_file "$NATIVE"
    printf '{"cache_root":"%s",\n"original_sha256":"%s",\n"patched_sha256":"%s",\n"english_sha256":"%s",\n"backup_path":"%s"}\n' \
        "$NATIVE" "$original_hash" "$patched_hash" "$english_hash" "$backup" >"$STATE_FILE"

    status="$(gui_status)"
    [[ "$status" == *'STATE=russian'* ]] || fail 'matching source and own patch were not reported as installed'

    printf 'english-after-update' >"${NATIVE}/${ENGLISH_NAME}"
    status="$(gui_status)"
    [[ "$status" != *'STATE=russian'* ]] || fail 'changed English source was reported as current Russian installation'

    # The catalog can build exactly the same UK bytes even after the EN source changed. In that case
    # the install action must reject the old patch before its "already installed" shortcut.
    refresh_overlay() { return 0; }
    fake_cli() {
        printf 'previous-translation' >"${WORK_DIR}/${TARGET_NAME}.ru"
        printf '{"applied_ru":1}\n' >"${WORK_DIR}/report.json"
    }
    CLI=fake_cli
    atomic_install() { printf 'unexpected write\n' >"${TEST_ROOT}/source-changed-write.txt"; return 0; }
    if do_install "$NATIVE"; then fail 'install accepted an old patch after the English source changed'; fi
    [ ! -f "${TEST_ROOT}/source-changed-write.txt" ] || fail 'install wrote a patch built from an obsolete UK file'
    [ "$(sha256_of "${NATIVE}/${TARGET_NAME}")" = "$patched_hash" ] || fail 'install changed the old patch unexpectedly'
    grep -q 'Английская таблица игры изменилась' "${TEST_ROOT}/error.txt" \
        || fail 'install did not explain why the matching old build was rejected'

    # Restoring B to the old official A is safe from overwriting a newer UK file, but A is still
    # obsolete relative to the new EN source. Tell the player to download UK again before playing.
    say_info() { printf '%s\n' "$1" >"${TEST_ROOT}/restore-info.txt"; }
    atomic_install() { cp "$1" "$2"; }
    do_restore "$NATIVE" || fail 'restore rejected its own old patch after an English-only update'
    [ "$(sha256_of "${NATIVE}/${TARGET_NAME}")" = "$original_hash" ] || fail 'restore did not return its verified backup'
    grep -q 'заново загрузите украинский язык' "${TEST_ROOT}/restore-info.txt" \
        || fail 'restore did not warn that the Ukrainian file must be refreshed'
    printf 'previous-translation' >"${NATIVE}/${TARGET_NAME}"

    # Legacy state files lack the source hash. Their LOC1 family and canonical Prod revisions must
    # still be checked instead of trusting the old UK hash on its own.
    printf '{"cache_root":"%s",\n"original_sha256":"%s",\n"patched_sha256":"%s",\n"backup_path":"%s"}\n' \
        "$NATIVE" "$original_hash" "$patched_hash" "$backup" >"$STATE_FILE"
    status="$(gui_status)"
    [[ "$status" == *'STATE=russian'* ]] || fail 'matching legacy state was not reported as installed'

    english_family=0.61.2
    status="$(gui_status)"
    [[ "$status" != *'STATE=russian'* ]] || fail 'different English LOC1 family was reported as installed'

    english_family=0.61.1
    english_revision=Prod_0.61.1_4
    status="$(gui_status)"
    [[ "$status" == *'STATE=russian'* ]] || fail 'same-family locale-specific Prod revisions were reported incompatible'

    english_revision=Prod_0.61.2_4
    status="$(gui_status)"
    [[ "$status" != *'STATE=russian'* ]] || fail 'English Prod revision from another family was reported installed'
)

printf 'PASS: mac installed status follows the current English source\n'
