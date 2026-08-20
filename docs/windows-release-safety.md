# Windows patcher release and trust model

## Player layout

InvokersRu 3.0 Preview is published as a normal self-contained .NET directory,
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
directories. The catalog must match the SHA-256 pin in the certified profile,
and the published CLI must prove that it embeds that exact profile before the
payload manifest is created.

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

- **unsigned local build** — allowed for developer testing, clearly warned and
  not suitable for Discord distribution;
- **signed public build** — GUI/project binaries, Setup and uninstaller are
  Authenticode-signed and RFC 3161 timestamped, then their final hashes are
  published with the GitHub Release.

For public distribution, use a certificate or managed signing service whose
publisher name the community can recognize. Keep release builds reproducible,
publish the Git commit and SHA-256 sidecar, and never add obfuscation, executable
downloads, in-process hooks or hidden background behavior merely to suppress a
warning.

## Release checklist

1. Build from a clean, reviewed commit.
2. Publish the signed player payload and inspect `BUILD-RECEIPT.json`.
3. Run `build-installer.ps1 -VerifyOnly -ExpectedSignerThumbprint <thumbprint>`
   to verify the signed payload. Do not pass an Inno Sign Tool in verify-only
   mode; no installer exists yet.
4. Compile the installer with the configured Inno Authenticode hook.
5. Confirm the final signature is valid and timestamped.
6. Test install, version detection, blocked unknown version, apply, restore and
   uninstall in a disposable Windows user profile.
7. Upload the installer and its `.sha256` sidecar to the same GitHub Release.
8. Have at least one second community maintainer verify the downloaded hash and
   publisher before posting the link in Discord.
