#!/usr/bin/env bash
# Packs the computer-side helper for Android into one archive a player can just unzip and run.
#
# The archive carries the APK as well: the helper needs the app on the phone to compose the file,
# and downloading it separately is one more step for someone to get wrong.
#
# Usage: scripts/build-pc-helper.sh [output-directory]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-${REPO_ROOT}/work/pc-helper}"
STAGE="${OUT_DIR}/Rusifikator-Invokers-PC"

APK="$(ls -t "${REPO_ROOT}"/work/android-apk/Rusifikator-Invokers-Android-*.apk 2>/dev/null | head -1)"
if [ -z "$APK" ]; then
    echo "APK не собран, собираю…"
    "${REPO_ROOT}/scripts/build-android-apk.sh" >/dev/null
    APK="$(ls -t "${REPO_ROOT}"/work/android-apk/Rusifikator-Invokers-Android-*.apk | head -1)"
fi

VERSION="$(/usr/bin/sed -n 's/.*android:versionName="\([^"]*\)".*/\1/p' \
    "${REPO_ROOT}/android/AndroidManifest.xml" | head -1)"

rm -rf "$OUT_DIR"
mkdir -p "$STAGE"

cp "${REPO_ROOT}/pc/install-android.sh" \
   "${REPO_ROOT}/pc/install-android.ps1" \
   "${REPO_ROOT}/pc/Русификатор-Android.cmd" \
   "${REPO_ROOT}/pc/Русификатор-Android.command" \
   "${REPO_ROOT}/pc/ПРОЧТИ-МЕНЯ.txt" \
   "$STAGE/"
cp "$APK" "$STAGE/"
chmod +x "${STAGE}/install-android.sh" "${STAGE}/Русификатор-Android.command"

ARCHIVE="${OUT_DIR}/Rusifikator-Invokers-PC.zip"
# Written with Python rather than zip(1) so the Unicode name flag is set: without it Windows
# Explorer decodes the Cyrillic file names in the OEM codepage and shows them as mojibake, which is
# exactly the moment a player gives up.
python3 - "$STAGE" "$ARCHIVE" <<'PYTHON'
import os, sys, zipfile

stage, archive = sys.argv[1], sys.argv[2]
root = os.path.basename(stage)
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in sorted(os.listdir(stage)):
        path = os.path.join(stage, name)
        info = zipfile.ZipInfo(f"{root}/{name}", date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        executable = os.access(path, os.X_OK)
        info.external_attr = (0o755 if executable else 0o644) << 16
        info.flag_bits |= 0x800
        with open(path, "rb") as handle:
            zf.writestr(info, handle.read())
PYTHON
rm -rf "$STAGE"

echo "Архив: ${ARCHIVE} ($(du -h "$ARCHIVE" | cut -f1)), приложение ${VERSION}"
