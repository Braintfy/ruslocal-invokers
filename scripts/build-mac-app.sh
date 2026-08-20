#!/usr/bin/env bash
# Assembles "Русификатор Invokers.app" and a .dmg around the self-contained CLI.
#
# The bundle deliberately ships no game data: the Russian file is composed on the user's own
# machine from their installed game plus the public source-free overlay, which the app downloads.
#
# Usage: scripts/build-mac-app.sh [output-directory]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-${REPO_ROOT}/work/mac-app}"
APP_NAME="Русификатор Invokers"
APP_DIR="${OUT_DIR}/${APP_NAME}.app"
# Both slices ship. Apple Silicon is where the iOS-on-Mac game lives, but an Intel Mac still has real
# work to do: it explains why the game cannot be there, and it can build and carry the translation to
# a connected Android phone. A single-slice bundle just refuses to launch, which explains nothing.
RIDS="osx-arm64 osx-x64"
MIN_MACOS="12.0"

DOTNET="${DOTNET:-$(command -v dotnet || echo "${HOME}/.dotnet/dotnet")}"
[ -x "$DOTNET" ] || { echo "ERROR: dotnet SDK not found; set DOTNET=/path/to/dotnet" >&2; exit 1; }

APP_VERSION="$(/usr/bin/sed -n 's/^BUNDLE_VERSION="\(.*\)"$/\1/p' "${REPO_ROOT}/mac/patcher-main.sh" | head -1)"
[ -n "$APP_VERSION" ] || { echo "ERROR: BUNDLE_VERSION missing from mac/patcher-main.sh" >&2; exit 1; }

# The published manifest must carry the checksum of the exact script clients will download, otherwise
# self-update refuses it. Keeping this in the build removes the chance of publishing a stale hash.
PATCHER_VERSION="$(/usr/bin/sed -n 's/^APP_VERSION="\(.*\)"$/\1/p' "${REPO_ROOT}/mac/patcher-main.sh" | head -1)"
PATCHER_SHA="$(shasum -a 256 "${REPO_ROOT}/mac/patcher-main.sh" | awk '{print toupper($1)}')"
# The oldest bundle that still works, which is not the same as the one being built: rebuilding the
# image must not tell every existing user to download it again.
MINIMUM_BUNDLE="$(/usr/bin/sed -n 's/^MINIMUM_BUNDLE_VERSION="\(.*\)"$/\1/p' "${REPO_ROOT}/mac/patcher-main.sh" | head -1)"
[ -n "$MINIMUM_BUNDLE" ] || { echo "ERROR: MINIMUM_BUNDLE_VERSION missing from mac/patcher-main.sh" >&2; exit 1; }
/usr/bin/python3 - "$REPO_ROOT" "$PATCHER_VERSION" "$PATCHER_SHA" "$MINIMUM_BUNDLE" <<'PY'
import json, sys
repo, version, digest, bundle = sys.argv[1:5]
path = f"{repo}/config/mac-patcher.json"
with open(path, encoding="utf-8") as handle:
    manifest = json.load(handle)
manifest["patcher_version"] = version
manifest["patcher_sha256"] = digest
manifest["minimum_bundle_version"] = bundle
with open(path, "w", encoding="utf-8") as handle:
    json.dump(manifest, handle, ensure_ascii=False, indent=2)
    handle.write("\n")
PY
echo "Manifest pinned: patcher ${PATCHER_VERSION} (${PATCHER_SHA:0:16}…), minimum bundle ${MINIMUM_BUNDLE}"

echo "Building ${APP_NAME}.app v${APP_VERSION} for ${RIDS}"
rm -rf "$APP_DIR"
mkdir -p "${APP_DIR}/Contents/MacOS" "${APP_DIR}/Contents/Resources"

# The repository pins an empty package source for offline, dependency-free builds. Publishing a
# self-contained binary additionally needs Microsoft's runtime pack, so allow nuget.org for this
# step only instead of loosening the checked-in NuGet.Config.
PUBLISH_CONFIG="$(mktemp -t invokersru-nuget)"
trap 'rm -f "$PUBLISH_CONFIG"' EXIT
cat > "$PUBLISH_CONFIG" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
XML

PUBLISH_DIR="${OUT_DIR}/publish"
rm -rf "$PUBLISH_DIR"
SLICES=""
for rid in $RIDS; do
    echo "  publishing ${rid}"
    "$DOTNET" publish "${REPO_ROOT}/src/InvokersRu.Cli/InvokersRu.Cli.csproj" \
        -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
        -p:PublishTrimmed=false -p:InvariantGlobalization=true -p:DebugType=none \
        -o "${PUBLISH_DIR}/${rid}" --configfile "$PUBLISH_CONFIG" >/dev/null
    [ -f "${PUBLISH_DIR}/${rid}/InvokersRu.Cli" ] || { echo "ERROR: ${rid} publish produced no binary" >&2; exit 1; }
    SLICES="${SLICES} ${PUBLISH_DIR}/${rid}/InvokersRu.Cli"
done

# .NET publishes one architecture at a time, so the fat binary is assembled here rather than by the SDK.
lipo -create $SLICES -output "${APP_DIR}/Contents/Resources/InvokersRu.Cli"
chmod 755 "${APP_DIR}/Contents/Resources/InvokersRu.Cli"
install -m 755 "${REPO_ROOT}/mac/patcher-main.sh" "${APP_DIR}/Contents/Resources/patcher.sh"

# The bundle's main executable must be a Mach-O binary. A shell script here cannot hold a Full Disk
# Access grant: the running process would be /bin/bash, so the switch in System Settings appears
# enabled while every read of the game container still fails.
cc -O2 -Wall -Wextra -arch arm64 -arch x86_64 -mmacosx-version-min="${MIN_MACOS}" \
   -o "${APP_DIR}/Contents/MacOS/${APP_NAME}" "${REPO_ROOT}/mac/launcher.c"
chmod 755 "${APP_DIR}/Contents/MacOS/${APP_NAME}"

# A missing slice is the failure that reaches users as an unexplained prohibitory icon in Finder, so
# it fails the build here instead.
for binary in "${APP_DIR}/Contents/MacOS/${APP_NAME}" "${APP_DIR}/Contents/Resources/InvokersRu.Cli"; do
    archs="$(lipo -archs "$binary")"
    case " $archs " in
        *" arm64 "*) ;;
        *) echo "ERROR: $(basename "$binary") has no arm64 slice (${archs})" >&2; exit 1 ;;
    esac
    case " $archs " in
        *" x86_64 "*) ;;
        *) echo "ERROR: $(basename "$binary") has no x86_64 slice (${archs})" >&2; exit 1 ;;
    esac
    echo "  $(basename "$binary"): ${archs}"
done

cat > "${APP_DIR}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>${APP_NAME}</string>
    <key>CFBundleDisplayName</key><string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key><string>community.invokersru.patcher</string>
    <key>CFBundleVersion</key><string>${APP_VERSION}</string>
    <key>CFBundleShortVersionString</key><string>${APP_VERSION}</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>${APP_NAME}</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>${MIN_MACOS}</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSHumanReadableCopyright</key><string>Unofficial community localization. Not affiliated with HitZone Inc.</string>
    <key>LSUIElement</key><false/>
</dict>
</plist>
PLIST

# A minimal generated icon keeps the bundle self-describing without committing binary art.
ICONSET="${OUT_DIR}/AppIcon.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
python3 - "$ICONSET" <<'PY'
import struct, sys, zlib, os

def png(path, size):
    # Flat brand-coloured square with a lighter inner block; enough to identify the app in Finder.
    bg, fg = (28, 32, 44), (214, 176, 92)
    rows = bytearray()
    pad = max(1, size // 5)
    for y in range(size):
        rows.append(0)
        for x in range(size):
            inner = pad <= x < size - pad and pad <= y < size - pad
            rows.extend(fg if inner else bg)
    def chunk(tag, data):
        payload = tag + data
        return struct.pack('>I', len(data)) + payload + struct.pack('>I', zlib.crc32(payload) & 0xFFFFFFFF)
    header = struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)
    with open(path, 'wb') as handle:
        handle.write(b'\x89PNG\r\n\x1a\n')
        handle.write(chunk(b'IHDR', header))
        handle.write(chunk(b'IDAT', zlib.compress(bytes(rows), 9)))
        handle.write(chunk(b'IEND', b''))

target = sys.argv[1]
for size in (16, 32, 128, 256, 512):
    png(os.path.join(target, f'icon_{size}x{size}.png'), size)
    png(os.path.join(target, f'icon_{size}x{size}@2x.png'), size * 2)
PY
iconutil -c icns "$ICONSET" -o "${APP_DIR}/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

# An ad-hoc signature keeps the bundle launchable after the user clears quarantine; it is not a
# Developer ID signature, so Gatekeeper still needs the documented right-click Open on first run.
codesign --force --deep --sign - "$APP_DIR" >/dev/null 2>&1 || echo "warning: ad-hoc codesign failed; the app still runs after right-click Open" >&2

# Only the current image should ever be present, so nobody hands out a superseded build.
find "$OUT_DIR" -maxdepth 1 -name 'Rusifikator-Invokers-*.dmg' -delete 2>/dev/null || true
DMG_PATH="${OUT_DIR}/Rusifikator-Invokers-${APP_VERSION}.dmg"
rm -f "$DMG_PATH"
STAGE="${OUT_DIR}/dmg-stage"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -R "$APP_DIR" "$STAGE/"
ln -s /Applications "${STAGE}/Applications"
cp "${REPO_ROOT}/mac/README-macos.txt" "${STAGE}/ПРОЧТИ МЕНЯ.txt"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG_PATH" >/dev/null
rm -rf "$STAGE" "$PUBLISH_DIR"

echo "App: ${APP_DIR}"
echo "DMG: ${DMG_PATH} ($(du -h "$DMG_PATH" | cut -f1))"
