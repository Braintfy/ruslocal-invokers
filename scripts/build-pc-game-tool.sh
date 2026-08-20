#!/usr/bin/env bash
# Packs the tool that translates the PC build of the game into one archive a player unzips and runs.
#
# Unlike the Android helper next to it, this one carries the catalog. The computer composes the file
# itself here, and a 35 MB download over plain HTTP is the least reliable step in the flow — shipping
# it means the tool works on the first try and offline, and a newer catalog is still fetched when the
# network allows.
#
# Usage: scripts/build-pc-game-tool.sh [output-directory]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-${REPO_ROOT}/work/pc-game-tool}"
STAGE="${OUT_DIR}/Rusifikator-Invokers-PC-Game"
RID="win-x64"

DOTNET="${DOTNET:-$(command -v dotnet || echo "${HOME}/.dotnet/dotnet")}"
[ -x "$DOTNET" ] || { echo "ERROR: dotnet SDK not found; set DOTNET=/path/to/dotnet" >&2; exit 1; }

CATALOG="${REPO_ROOT}/translations/ru_RU.jsonl"
[ -s "$CATALOG" ] || { echo "ERROR: ${CATALOG} missing" >&2; exit 1; }

rm -rf "$OUT_DIR"
mkdir -p "$STAGE"

# The repository pins an empty package source for offline builds; publishing self-contained needs
# Microsoft's runtime pack, so nuget.org is allowed for this step only.
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

echo "Publishing ${RID}"
PUBLISH_DIR="${OUT_DIR}/publish"
"$DOTNET" publish "${REPO_ROOT}/src/InvokersRu.Cli/InvokersRu.Cli.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false -p:InvariantGlobalization=true -p:DebugType=none \
    -o "$PUBLISH_DIR" --configfile "$PUBLISH_CONFIG" >/dev/null

EXE="${PUBLISH_DIR}/InvokersRu.Cli.exe"
[ -f "$EXE" ] || { echo "ERROR: publish produced no InvokersRu.Cli.exe" >&2; exit 1; }
case "$(file -b "$EXE")" in
    *"PE32+"*) ;;
    *) echo "ERROR: InvokersRu.Cli.exe is not a Windows binary" >&2; exit 1 ;;
esac

cp "$EXE" "$STAGE/"
cp "$CATALOG" "${STAGE}/ru_RU.jsonl"
cp "${REPO_ROOT}/pc/install-pc.ps1" \
   "${REPO_ROOT}/pc/Русификатор-ПК.cmd" \
   "${REPO_ROOT}/pc/ПРОЧТИ-МЕНЯ-ПК.txt" \
   "$STAGE/"

ARCHIVE="${OUT_DIR}/Rusifikator-Invokers-PC-Game.zip"
# Written with Python rather than zip(1) so the Unicode name flag is set: without it Windows Explorer
# decodes the Cyrillic file names in the OEM codepage and shows mojibake, which is exactly the moment
# a player gives up.
python3 - "$STAGE" "$ARCHIVE" <<'PYTHON'
import os, sys, zipfile

stage, archive = sys.argv[1], sys.argv[2]
root = os.path.basename(stage)
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in sorted(os.listdir(stage)):
        path = os.path.join(stage, name)
        info = zipfile.ZipInfo(f"{root}/{name}", date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = (0o755 if os.access(path, os.X_OK) else 0o644) << 16
        info.flag_bits |= 0x800
        with open(path, "rb") as handle:
            zf.writestr(info, handle.read())
PYTHON

rm -rf "$STAGE" "$PUBLISH_DIR"
echo "Архив: ${ARCHIVE} ($(du -h "$ARCHIVE" | cut -f1))"
