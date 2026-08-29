#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_ROOT="$(mktemp -d /private/tmp/invokersru-mac-test.XXXXXX)"
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
