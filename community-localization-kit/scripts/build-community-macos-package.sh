#!/usr/bin/env bash
# Run on macOS. Builds a source-auditable package/DMG that composes LOC1 from the user's own game.

set -euo pipefail

usage() {
    cat >&2 <<'EOF'
Usage: build-community-macos-package.sh CONFIG CATALOG CERTIFIED_PROFILE BUILD_RECEIPT OUTPUT_DIR [VERSION]

OUTPUT_DIR must be a new directory below the repository work/ directory.
The package contains no original EN/base LOC1 and no prebuilt game-derived localization table.
EOF
    exit 2
}

[ "$#" -ge 5 ] && [ "$#" -le 6 ] || usage
CONFIG="$1"
CATALOG="$2"
PROFILE="$3"
BUILD_RECEIPT="$4"
OUT_DIR="$5"
VERSION="${6:-1.0.0}"

KIT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${KIT_ROOT}/.." && pwd)"
WORK_ROOT="$(cd "${REPO_ROOT}/work" && pwd -P)"
DOTNET="${DOTNET:-$(command -v dotnet || true)}"
PYTHON="${PYTHON:-$(command -v python3 || true)}"

[ "$(uname -s)" = Darwin ] || { echo 'ERROR: the macOS package builder must run on macOS.' >&2; exit 1; }
[[ "$VERSION" =~ ^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(-[A-Za-z0-9][A-Za-z0-9.-]{0,31})?$ ]] || { echo 'ERROR: invalid semantic version.' >&2; exit 1; }
[ -x "$DOTNET" ] || { echo 'ERROR: .NET 10 SDK not found; set DOTNET=/path/to/dotnet.' >&2; exit 1; }
[ -x "$PYTHON" ] || { echo 'ERROR: python3 is required by the local build-time verifier.' >&2; exit 1; }

regular() { [ -f "$1" ] && [ ! -L "$1" ] && [ -s "$1" ] || { echo "ERROR: $2 must be a non-empty regular file: $1" >&2; exit 1; }; }
regular "$CONFIG" 'language config'
regular "$CATALOG" 'source-free catalog'
regular "$PROFILE" 'certified local profile'
regular "$BUILD_RECEIPT" 'exact community build receipt'
CONFIG="$(cd "$(dirname "$CONFIG")" && pwd)/$(basename "$CONFIG")"
CATALOG="$(cd "$(dirname "$CATALOG")" && pwd)/$(basename "$CATALOG")"
PROFILE="$(cd "$(dirname "$PROFILE")" && pwd)/$(basename "$PROFILE")"
BUILD_RECEIPT="$(cd "$(dirname "$BUILD_RECEIPT")" && pwd)/$(basename "$BUILD_RECEIPT")"
OUT_PARENT="$(cd "$(dirname "$OUT_DIR")" 2>/dev/null && pwd -P || true)"
[ -n "$OUT_PARENT" ] || { echo 'ERROR: output parent must already exist.' >&2; exit 1; }
OUT_DIR="${OUT_PARENT}/$(basename "$OUT_DIR")"
case "$OUT_DIR" in "${WORK_ROOT}"/*) ;; *) echo "ERROR: output must stay below ${WORK_ROOT}." >&2; exit 1 ;; esac
[ ! -e "$OUT_DIR" ] && [ ! -L "$OUT_DIR" ] || { echo "ERROR: output already exists: $OUT_DIR" >&2; exit 1; }
ARCHIVE="${OUT_DIR}.tar.gz"
DMG="${OUT_DIR}.dmg"
[ ! -e "$ARCHIVE" ] && [ ! -L "$ARCHIVE" ] && [ ! -e "$DMG" ] && [ ! -L "$DMG" ] \
    || { echo 'ERROR: archive or DMG output already exists or is a symlink.' >&2; exit 1; }

TEMP_DIR="$(mktemp -d "${WORK_ROOT}/.community-mac.XXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT
PUBLISH="${TEMP_DIR}/publish"
mkdir -p "$OUT_DIR" "$PUBLISH"

PINS="${OUT_DIR}/community-pins.txt"
"$PYTHON" - "$CONFIG" "$PROFILE" "$CATALOG" "$BUILD_RECEIPT" "$PINS" <<'PY'
import hashlib, json, os, re, sys

config_path, profile_path, catalog_path, receipt_path, pins_path = sys.argv[1:]

def object_no_duplicates(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise SystemExit(f"ERROR: duplicate JSON member: {key}")
        value[key] = item
    return value

def load(path):
    with open(path, 'r', encoding='utf-8', errors='strict') as handle:
        value = json.load(handle, object_pairs_hook=object_no_duplicates)
    if not isinstance(value, dict):
        raise SystemExit('ERROR: config/profile root must be an object')
    return value

def sha(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest().upper()

def exact_keys(value, expected, label):
    if not isinstance(value, dict) or set(value) != set(expected):
        raise SystemExit(f'ERROR: {label} members differ from the exact schema')

config, profile, receipt = load(config_path), load(profile_path), load(receipt_path)
exact_keys(config, ('schema', 'pack_id', 'target_language', 'injection_slot', 'catalog_policy',
                    'fallback', 'allow_per_locale_content_version'), 'language config')
exact_keys(config.get('target_language'), ('name', 'bcp47'), 'target_language')
exact_keys(config.get('injection_slot'), ('locale', 'file', 'stamp_file', 'locale_id'), 'injection_slot')
exact_keys(receipt, ('schema', 'kind', 'pack_id', 'target_language', 'injection_slot', 'catalog_policy',
                     'fallback', 'allow_per_locale_content_version', 'language_config_sha256', 'profile_id',
                     'game_version', 'catalog_sha256', 'output_raw_sha256', 'profile_sha256', 'entry_count',
                     'applied_translations', 'english_fallbacks', 'base_fallbacks', 'needs_review_fallbacks',
                     'policy', 'officially_signed'), 'exact build receipt')
exact_keys(receipt.get('target_language'), ('name', 'bcp47'), 'receipt target_language')
exact_keys(receipt.get('injection_slot'), ('locale', 'file', 'stamp_file', 'locale_id'), 'receipt injection_slot')
slot = config.get('injection_slot')
if (config.get('schema') != 1 or not isinstance(slot, dict)
        or slot != {'locale': 'uk_UA', 'file': 'dl_uk_UA.bin', 'stamp_file': 'dl_uk_UA.bin.ver', 'locale_id': 8}
        or config.get('catalog_policy') not in ('preview-drafts', 'release-approved')
        or config.get('fallback') != 'english'
        or type(config.get('allow_per_locale_content_version')) is not bool):
    raise SystemExit('ERROR: config is not the audited EN -> uk_UA-slot schema')
if (profile.get('schema') != 1 or profile.get('certified') is not True or profile.get('readiness') != 'ready'
        or profile.get('english_locale_id') != 1 or profile.get('base_locale_id') != 8
        or profile.get('translation_policy') not in ('community-preview-all-drafts', 'release-approved')):
    raise SystemExit('ERROR: profile is not an exact ready/certified EN -> uk_UA community profile')
if sha(catalog_path) != str(profile.get('translation_catalog_sha256', '')).upper():
    raise SystemExit('ERROR: catalog SHA-256 does not match the exact profile')
pack_id = config.get('pack_id', '')
target_name = config.get('target_language', {}).get('name', '')
bcp47 = config.get('target_language', {}).get('bcp47', '')
if not re.fullmatch(r'[a-z0-9][a-z0-9._-]{1,63}', pack_id):
    raise SystemExit('ERROR: unsafe pack_id')
if not re.fullmatch(r'[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*', bcp47):
    raise SystemExit('ERROR: unsafe target BCP 47 tag')
if not isinstance(target_name, str) or not (1 <= len(target_name.strip()) <= 80) or any(ord(c) < 32 for c in target_name):
    raise SystemExit('ERROR: unsafe target language name')
hash_names = ('english_sha256', 'base_sha256', 'stamp_sha256', 'expected_output_sha256')
for name in hash_names:
    if not re.fullmatch(r'[A-Fa-f0-9]{64}', str(profile.get(name, ''))):
        raise SystemExit(f'ERROR: invalid profile hash: {name}')
policy = 'preview-drafts' if profile['translation_policy'] == 'community-preview-all-drafts' else 'release-approved'
config_hash, profile_hash, catalog_hash = sha(config_path), sha(profile_path), sha(catalog_path)
count_names = ('entry_count', 'applied_translations', 'english_fallbacks', 'base_fallbacks', 'needs_review_fallbacks')
if any(type(receipt.get(name)) is not int or receipt[name] < 0 for name in count_names):
    raise SystemExit('ERROR: exact build receipt contains invalid composition counts')
if receipt['applied_translations'] + receipt['english_fallbacks'] + receipt['base_fallbacks'] != receipt['entry_count']:
    raise SystemExit('ERROR: exact build receipt composition does not partition the LOC1 entries')
expected_receipt = (
    receipt.get('schema') == 2
    and receipt.get('kind') == 'community-localization-exact-build'
    and receipt.get('pack_id') == pack_id
    and receipt.get('target_language') == config.get('target_language')
    and receipt.get('injection_slot') == slot
    and receipt.get('catalog_policy') == config.get('catalog_policy')
    and receipt.get('fallback') == config.get('fallback')
    and type(receipt.get('allow_per_locale_content_version')) is bool
    and receipt.get('allow_per_locale_content_version') == config.get('allow_per_locale_content_version')
    and str(receipt.get('language_config_sha256', '')).upper() == config_hash
    and receipt.get('profile_id') == profile.get('id')
    and receipt.get('game_version') == profile.get('game_version')
    and str(receipt.get('catalog_sha256', '')).upper() == catalog_hash
    and str(receipt.get('output_raw_sha256', '')).upper() == str(profile.get('expected_output_sha256', '')).upper()
    and str(receipt.get('profile_sha256', '')).upper() == profile_hash
    and receipt.get('entry_count') == profile.get('entry_count')
    and receipt.get('applied_translations') == profile.get('expected_applied_translations')
    and receipt.get('english_fallbacks') == profile.get('expected_english_fallbacks')
    and receipt.get('base_fallbacks') == profile.get('expected_base_fallbacks')
    and receipt.get('needs_review_fallbacks') == profile.get('expected_needs_review_fallbacks')
    and receipt.get('policy') == profile.get('translation_policy')
    and profile.get('translation_policy') == ('community-preview-all-drafts' if config.get('catalog_policy') == 'preview-drafts' else 'release-approved')
    and receipt.get('officially_signed') is False
)
if not expected_receipt:
    raise SystemExit('ERROR: config, catalog, profile, and exact build receipt are not one content-bound language pack')
values = {
    'schema': '1',
    'pack_id': pack_id,
    'target_bcp47': bcp47,
    'catalog_policy': policy,
    'allow_per_locale_content_version': 'true' if config.get('allow_per_locale_content_version') is True else 'false',
    'english_sha256': profile['english_sha256'].upper(),
    'base_sha256': profile['base_sha256'].upper(),
    'stamp_sha256': profile['stamp_sha256'].upper(),
    'catalog_sha256': catalog_hash,
    'expected_output_sha256': profile['expected_output_sha256'].upper(),
    'language_config_sha256': config_hash,
    'profile_sha256': profile_hash,
    'source_build_receipt_sha256': sha(receipt_path),
}
with open(pins_path, 'x', encoding='ascii', newline='\n') as handle:
    for key, value in values.items():
        handle.write(f'{key}={value}\n')
PY

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
"$DOTNET" restore "${REPO_ROOT}/src/InvokersRu.Cli/InvokersRu.Cli.csproj" --runtime osx-arm64 \
    --configfile "${REPO_ROOT}/NuGet.Config" --source https://api.nuget.org/v3/index.json \
    -p:SignedUpdateChannelConfigPath=
"$DOTNET" publish "${REPO_ROOT}/src/InvokersRu.Cli/InvokersRu.Cli.csproj" -c Release -r osx-arm64 \
    --self-contained true --no-restore -o "$PUBLISH" -p:Version="$VERSION" \
    -p:SignedUpdateChannelConfigPath= \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false

find "$PUBLISH" -maxdepth 1 -type f ! -name '*.pdb' ! -name 'createdump' -exec cp '{}' "$OUT_DIR/" ';'
mkdir -p "${OUT_DIR}/translations" "${OUT_DIR}/profiles"
cp "$CATALOG" "${OUT_DIR}/translations/ru_RU.jsonl"
cp "$PROFILE" "${OUT_DIR}/profiles/certified-runtime-profile.json"
cp "$BUILD_RECEIPT" "${OUT_DIR}/profiles/community-build-receipt.json"
cp "$CONFIG" "${OUT_DIR}/language-config.json"
cp "${KIT_ROOT}/runtime/community-macos-patcher.sh" "${OUT_DIR}/CommunityLocalization.command"
cp "${KIT_ROOT}/runtime/PACKAGE-README-macos.txt" "${OUT_DIR}/README.txt"
cp "${REPO_ROOT}/LICENSE" "${OUT_DIR}/LICENSE.txt"
chmod 755 "${OUT_DIR}/InvokersRu.Cli" "${OUT_DIR}/CommunityLocalization.command"

"${OUT_DIR}/InvokersRu.Cli" --help | grep -q 'Disabled in this unsigned dev build' || {
    echo 'ERROR: packaged macOS CLI did not prove its game-write-disabled build mode.' >&2
    exit 1
}
UPDATE_STATUS_JSON="${TEMP_DIR}/update-status.json"
set +e
"${OUT_DIR}/InvokersRu.Cli" update-status --json >"$UPDATE_STATUS_JSON" 2>/dev/null
UPDATE_STATUS_EXIT=$?
set -e
[ "$UPDATE_STATUS_EXIT" -eq 5 ] || { echo 'ERROR: community CLI unexpectedly enabled a signed-update channel.' >&2; exit 1; }
"$PYTHON" - "$UPDATE_STATUS_JSON" <<'PY'
import json, sys
with open(sys.argv[1], 'r', encoding='utf-8', errors='strict') as handle:
    value = json.load(handle)
if (not isinstance(value, dict) or value.get('configured') is not False
        or value.get('network_status') != 'not-configured' or value.get('channel') is not None):
    raise SystemExit('ERROR: community CLI contains an official or ambient signed-update channel')
PY

(cd "$OUT_DIR" && find . -type f ! -name SHA256SUMS.txt -print | LC_ALL=C sort | while IFS= read -r file; do shasum -a 256 "$file"; done > SHA256SUMS.txt)
(cd "$(dirname "$OUT_DIR")" && tar -czf "$ARCHIVE" "$(basename "$OUT_DIR")")

STAGE="${TEMP_DIR}/dmg-stage"
mkdir -p "$STAGE"
cp -R "$OUT_DIR" "${STAGE}/Community Localization"
hdiutil create -volname "Invokers Community Localization" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null

printf 'macOS package: %s\n' "$ARCHIVE"
printf 'macOS DMG:     %s\n' "$DMG"
printf 'Both artifacts are unsigned local builds and contain no original game localization files.\n'
