#!/usr/bin/env bash
# Builds the Android patcher APK without Gradle: aapt2, javac, d8 and apksigner directly.
#
# Keeping the toolchain this thin means the build pulls nothing from Maven, so it stays reproducible
# and has no third-party code in it at all.
#
# Usage: scripts/build-android-apk.sh [output-directory]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-${REPO_ROOT}/work/android-apk}"
SRC_DIR="${REPO_ROOT}/android"

SDK="${ANDROID_HOME:-${HOME}/Library/Android/sdk}"
[ -d "$SDK" ] || { echo "ERROR: Android SDK not found at ${SDK}" >&2; exit 1; }
BUILD_TOOLS="$(ls -d "${SDK}/build-tools"/* 2>/dev/null | sort -V | tail -1)"
[ -n "$BUILD_TOOLS" ] || { echo "ERROR: no build-tools in ${SDK}" >&2; exit 1; }
PLATFORM="$(ls -d "${SDK}/platforms"/android-* 2>/dev/null | sort -V | tail -1)"
[ -n "$PLATFORM" ] || { echo "ERROR: no platform in ${SDK}" >&2; exit 1; }

JAVA_HOME="${JAVA_HOME:-/Applications/Android Studio.app/Contents/jbr/Contents/Home}"
[ -x "${JAVA_HOME}/bin/javac" ] || { echo "ERROR: JDK not found at ${JAVA_HOME}" >&2; exit 1; }
# d8, aapt2 and apksigner are launcher scripts that look for java on PATH rather than in JAVA_HOME.
export JAVA_HOME
export PATH="${JAVA_HOME}/bin:${PATH}"

VERSION="$(/usr/bin/sed -n 's/.*android:versionName="\([^"]*\)".*/\1/p' "${SRC_DIR}/AndroidManifest.xml" | head -1)"
[ -n "$VERSION" ] || VERSION="1.0.0"

echo "Building Rusifikator-Invokers-Android ${VERSION}"
rm -rf "$OUT_DIR"
mkdir -p "${OUT_DIR}/classes" "${OUT_DIR}/dex"

echo "  compiling…"
"${JAVA_HOME}/bin/javac" -source 17 -target 17 -nowarn \
    -classpath "${PLATFORM}/android.jar" \
    -d "${OUT_DIR}/classes" \
    "${SRC_DIR}"/src/ru/invokers/patcher/*.java

echo "  dexing…"
"${BUILD_TOOLS}/d8" --release --min-api 26 --lib "${PLATFORM}/android.jar" \
    --output "${OUT_DIR}/dex" \
    $(find "${OUT_DIR}/classes" -name '*.class')

echo "  packaging…"
"${BUILD_TOOLS}/aapt2" link \
    -I "${PLATFORM}/android.jar" \
    --manifest "${SRC_DIR}/AndroidManifest.xml" \
    --min-sdk-version 26 --target-sdk-version 35 \
    --version-name "$VERSION" \
    -o "${OUT_DIR}/base.apk" >/dev/null

(cd "${OUT_DIR}/dex" && "${BUILD_TOOLS}/aapt2" version >/dev/null 2>&1 || true)
(cd "${OUT_DIR}/dex" && zip -q "${OUT_DIR}/base.apk" classes.dex)

echo "  signing…"
# The keystore lives outside the repository and is created once. Android refuses to update an app
# whose signature changed, so regenerating a key per build would strand everyone who already
# installed it — they would have to uninstall first and lose the saved original file.
KEYSTORE="${INVOKERSRU_KEYSTORE:-${HOME}/.config/invokersru/android-release.keystore}"
mkdir -p "$(dirname "$KEYSTORE")"
if [ ! -f "$KEYSTORE" ]; then
    echo "  создаю ключ подписи (один раз): ${KEYSTORE}"
    "${JAVA_HOME}/bin/keytool" -genkeypair -v -keystore "$KEYSTORE" \
        -alias invokersru -keyalg RSA -keysize 2048 -validity 10000 \
        -storepass invokersru -keypass invokersru \
        -dname "CN=InvokersRu Community, OU=Localization, O=InvokersRu, C=RU" >/dev/null 2>&1
    echo "  СОХРАНИТЕ ЭТОТ ФАЙЛ: без него нельзя выпустить обновление приложения."
fi

ALIGNED="${OUT_DIR}/aligned.apk"
"${BUILD_TOOLS}/zipalign" -f -p 4 "${OUT_DIR}/base.apk" "$ALIGNED"

APK="${OUT_DIR}/Rusifikator-Invokers-Android-${VERSION}.apk"
"${BUILD_TOOLS}/apksigner" sign --ks "$KEYSTORE" --ks-pass pass:invokersru --key-pass pass:invokersru \
    --out "$APK" "$ALIGNED"
"${BUILD_TOOLS}/apksigner" verify "$APK" >/dev/null

rm -f "${OUT_DIR}/base.apk" "$ALIGNED" "${APK}.idsig"
rm -rf "${OUT_DIR}/classes" "${OUT_DIR}/dex"

echo "APK: ${APK} ($(du -h "$APK" | cut -f1))"
