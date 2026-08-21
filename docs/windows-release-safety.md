# Windows patcher release and trust model

## Player layout

InvokersRu 3.1 Preview is published as a normal self-contained .NET directory,
not as a packed single executable. The GUI and supervised CLI remain adjacent so
the GUI can resolve its fixed companion path. The translation and compatibility
evidence are installed as data:

```text
InvokersRu\
  InvokersRu.Gui.exe
  InvokersRu.Cli.exe
  InvokersRu.*.dll
  .NET runtime DLLs
  translations\ru_RU.jsonl
  profiles\runtime-cache-profile.0.60.1247.json
  BUILD-RECEIPT.json
  PAYLOAD-SHA256.json
  LICENSE.txt
  README.txt
```

`PAYLOAD-SHA256.json` is the exact allowlist. A release build is rejected if a
path is added, removed, renamed, linked or changed after the manifest is made.
Only the GUI and supervised CLI may be executable files. Script launchers,
shortcuts, nested installers and archives are forbidden from the installed
payload.

## Build boundary

The publishing script accepts only repository inputs and a new output below
`work`. It rejects reparse points and all known game, launcher and patch-state
directories. The catalog must match the SHA-256 pin in the certified bootstrap
profile, and the published CLI must prove that it embeds that profile plus the
bounded `compatible-revision` trust rules before the payload manifest is created.

The CLI also embeds one reviewed signed-update channel URL, key ID and P-256
public key. Translation catalogs and optional exact compatibility profiles downloaded at
runtime are accepted only after the detached signature, monotonic sequence,
size and SHA-256 pins have been verified. The private data-signing key is never
part of the repository or player payload. This data signature is independent of
Authenticode code signing.

The installer build copies the manifest-listed files one by one to a new staging
directory, verifies every SHA-256 again, runs Inno Setup and re-verifies the
staging tree after compilation. It never runs the generated installer.

## Installer behavior

The Inno Setup definition is intentionally uneventful:

- one fixed per-user destination:
  `%LOCALAPPDATA%\Programs\InvokersRu`;
- no administrator request or override;
- destination selection and previous-directory reuse disabled, with a minimal
  `[Code]` guard that rejects `/DIR`, known game/cache roots and any existing
  junction/symlink component even in silent mode;
- no `[Run]`, registry payload, service or scheduled task;
- no automatic closing/restarting of the game;
- no network call;
- LZMA normal compression with solid compression disabled;
- one Start Menu shortcut and a standard per-user uninstaller.

The patcher performs game checks and mutations only after the player starts it
and explicitly clicks the relevant action. Packaging never modifies game files.

## Authenticode and Defender expectations

No technical setting can guarantee zero antivirus or SmartScreen warnings for a
brand-new community executable. The release process therefore separates two
states:

- **unsigned community preview (current 3.1 mode)** — technically functional and
  shown as an unknown publisher by Windows; distribute it only with the exact Git
  commit, installer SHA-256 sidecar, an explicit SmartScreen warning, and no claim
  that the EXE is signed;
- **signed public build** — GUI/project binaries, Setup and uninstaller are
  Authenticode-signed and RFC 3161 timestamped, then their final hashes are
  published with the GitHub Release.

Authenticode is recommended and planned, but not represented as complete for the
current preview. When it becomes available, use a certificate or managed signing
service whose publisher name the community can recognize. In both modes keep
release builds reproducible, publish the Git commit and SHA-256 sidecar, and never
add obfuscation, executable downloads, in-process hooks or hidden background
behavior merely to suppress a warning.

## Release checklist

1. Build from a clean, reviewed commit.
2. Inspect `BUILD-RECEIPT.json` and run `build-installer.ps1 -VerifyOnly`.
3. For the current unsigned preview, compile without a Sign Tool, verify the
   `.sha256` sidecar, and label the GitHub Release/Discord post as unsigned.
4. For a future signed release, first verify the signed player payload with
   `-ExpectedSignerThumbprint`, then compile with the configured Inno Authenticode
   hook and confirm the final signature plus RFC 3161 timestamp.
5. Test install, exact version detection, a compatible-revision update with
   English fallback, refusal of an incompatible LOC1 family, apply, restore,
   recovery and uninstall in a disposable Windows user profile.
6. Upload the installer and its `.sha256` sidecar to the same GitHub Release.
7. Have at least one second community maintainer verify the downloaded hash and,
   when Authenticode is used, the publisher before posting the link in Discord.
