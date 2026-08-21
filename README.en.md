# Invokers: Titan Legacy Russian localization

[Русская версия](README.md)

An unofficial Russian translation of the game. **41,037 of 41,292 strings** are translated: menus, quests, skill descriptions, items, and battle messages. One string remains in English and another 254 entries are empty in the game itself.

The translation is machine-generated and has not yet received a complete human editorial pass. This project does not distribute original game files: Russian text is composed on each user's device from that user's own installed game data.

## Download

| Where you play | What to download |
| --- | --- |
| Mac | **[Russian localizer for macOS](https://github.com/Braintfy/ruslocal-invokers/releases/latest)** — the `.dmg` file |
| Android phone, no root | **[Rusifikator-Invokers-PC.zip](https://github.com/Braintfy/ruslocal-invokers/releases/latest)** — a computer helper; the phone app is included |
| Android phone with root | **[Rusifikator-Invokers-Android.apk](https://github.com/Braintfy/ruslocal-invokers/releases/latest)** |
| Windows PC with the PC game installed | **[InvokersRu for Windows](https://github.com/Braintfy/ruslocal-invokers/releases/latest)** — download `InvokersRu-…-win-x64.exe` |

The Mac version can update itself. The Android computer helper fetches the current translation from the project. Windows 3.1 ships an offline bootstrap translation and can fetch newer **signed translation data and exact game-version profiles** from the fixed GitHub channel without replacing the EXE. It detects the game version and blocks installation when the profile is missing, incompatible, expired, or requires a newer patcher.

The Mac game is the iPhone/iPad application. The App Store installs those applications only on Apple Silicon Macs (M1 or newer), so the game cannot be installed on an Intel Mac. The localizer itself is universal: on Intel it explains this limitation, and it can still help install the localization on a connected Android phone.

## Android installation

The Android translation works and has been tested. The installation method depends on whether the phone has root access; this is an Android platform restriction, not a project decision.

Starting with Android 11, one application cannot read or modify another application's data. Root is the only way to do this entirely on the phone. Without root, a computer transports the files, while the phone still performs the translation build. No third-party utility needs to be installed manually; the helper downloads what it needs.

### Method 1 — phone only (root required)

1. Download and install the APK.
2. In the game, select **Ukrainian**, wait for the text to download, and fully close the game.
3. Open “Русификатор Invokers,” choose Install translation, and grant root access.

### Method 2 — wireless over Wi-Fi

The phone and computer must be on the same Wi-Fi network. On Windows this is often more reliable than a cable because it needs no device driver.

1. Download `Rusifikator-Invokers-PC.zip` to the computer and extract it.
2. Run **`Русификатор-Android.cmd`** on Windows or **`Русификатор-Android.command`** on Mac. On first launch on Mac, right-click it and choose Open.
3. Enable Developer options on the phone. If the menu is hidden, open Settings → About phone → Software information, tap Build number seven times, and enter the device PIN.
4. Open Settings → Developer options and enable **Wireless debugging**.
5. Choose Wi-Fi in the helper. When it asks for an address and code, open Wireless debugging → Pair device with pairing code on the phone. The code is short-lived.
6. In the game, select **Ukrainian**, wait for the download, and fully close the game.
7. Choose Install translation. The phone may briefly show the builder application while the operation completes.

### Method 3 — USB cable

Steps 1–2 are the same, then:

3. Enable Developer options and **USB debugging**.
4. Connect a data-capable USB cable, not a charge-only cable.
5. Accept “Allow USB debugging?” on the phone; optionally select Always allow from this computer.
6. In the game, select **Ukrainian**, wait for the download, and fully close the game.
7. Choose USB cable in the helper and install the translation.

On **Samsung**, first disable Auto Blocker in Security and privacy; it blocks USB access by default. On **Xiaomi**, also enable “USB debugging (Security settings).”

### Restore the original on Android

In the same helper, option 2 restores its saved copy and option 3 removes the language data so the game downloads an official copy again.

The terminal equivalent is `scripts/android-patch.sh` with `status`, `apply`, `restore`, and `reset`. See [docs/android-client.md](docs/android-client.md) for the technical rationale.

## Windows PC installation

This path is for a game installed on the PC itself. The patcher finds the standard localization cache, detects the exact client version, and permits installation only when the certified profile and all SHA-256 values match.

> **Before localization:** select **Ukrainian** in the game, wait for its text to download, then fully quit both the game and launcher, including the launcher's system-tray icon.

1. Download `InvokersRu-…-win-x64.exe` from the [latest release](https://github.com/Braintfy/ruslocal-invokers/releases/latest) and install it. Administrator rights are not required.
2. Open the game settings, select **Ukrainian**, and wait until the text finishes downloading. Russian uses the Ukrainian slot because it is the game's existing Cyrillic slot.
3. Fully close the game and launcher, including the launcher icon in the system tray.
4. Start **InvokersRu** from the Start menu, select **Check**, then **Install localization**.

The patcher shows the detected/supported game versions, translated and English-fallback counts, and backup state. An unknown version, running game/launcher, mismatched catalog, stale patcher, or interrupted transaction blocks all writes. There is no force mode.

The exact original Ukrainian file is stored below `%LOCALAPPDATA%\InvokersRussian\runtime-cache`. Restore original accepts only the verified backup associated with the current recorded state.

## macOS installation

1. Open the DMG and drag **Русификатор Invokers** to Applications.
2. On first launch, right-click the icon, choose **Open**, and confirm. A normal double-click is blocked because the app has no paid Apple Developer ID signature. This step is needed once.
3. Start the game, select **Ukrainian**, wait for the download, and fully quit the game with Cmd+Q. Russian uses the Ukrainian slot because it is the game's existing Cyrillic slot.
4. Launch the localizer and choose Install translation.
5. The app requests access to game data and opens the appropriate settings page. Enable the switch for Русификатор Invokers. Installation resumes automatically. If the app is absent from the list, choose `+` and add it from Applications.

## One rule you must not break

**Do not open the in-game language selector after installing the localization.**

Selecting any language makes the client download that language file again and removes the replacement. Keep the selected language on Ukrainian. If the localization disappears, reinstalling it usually fixes the issue.

## Updates

### Windows 3.1 signed data channel

The Windows application checks one pinned GitHub data-channel URL. Translation catalogs and exact profiles for newer game builds are authenticated by a separate project ECDSA P-256 key. The patcher verifies the signature, monotonically increasing release sequence, expiry, size limits, download origin, and SHA-256 of every artifact before using it. A normal compatible catalog/profile update therefore does not require a new EXE.

If you already installed the older Windows 3.0 patcher, replace it with 3.1 once: version 3.0 does not implement the signed data-channel protocol. After that migration, compatible catalog/profile updates do not require another EXE.

The patcher reports its own compatibility separately. If it is below the signed manifest's minimum version, new data is blocked and the UI directs the player to [Releases](https://github.com/Braintfy/ruslocal-invokers/releases/latest). A new installer is also required when code must change — for example, if the game changes LOC1 schema, locale slot, cache path, or another invariant that signed data alone cannot safely express.

When the network is unavailable, the patcher can use its exact embedded bootstrap or authenticated last-known-good data under the documented expiry/state rules. It never treats an unsigned GitHub file or arbitrary URL as an update.

The data signature is not Authenticode. Unsigned Windows EXEs can still show `Unknown publisher` or SmartScreen reputation warnings until a publisher certificate/signing process is added.

### macOS

The macOS app checks the repository and can fetch a newer translation and updateable patcher driver. Reinstalling the app and granting disk access again is normally unnecessary because the updateable driver lives outside the app bundle. A download is accepted only when it matches the published checksum.

Disable self-update with:

```sh
touch ~/Library/Application\ Support/InvokersRu/no-self-update
```

A new DMG is needed only when the app explicitly says its fixed launcher changed. In that case, macOS Full Disk Access must be granted to the new build: remove the old row with `−`, use `+`, and add the app again.

## After a game update

A game update usually restores the official language file. On Windows, select Ukrainian again, wait for the download, fully quit the game and launcher, and choose Check. If an exact signed profile has been published, the patcher fetches the compatible data and offers Update localization. If no profile exists yet, all writes remain blocked; once the profile is published, checking again is enough and the patcher itself need not be reinstalled.

On macOS and Android, reinstall the localization after a compatible game update. If the update changed source strings, some text may remain English until the overlay is adapted.

## Restore the original

On Windows and Mac, use Restore original in the application. On Android, use options 2 or 3 in the computer helper. The Windows patcher restores only an exact backup linked to its recorded state.

To remove the Mac localizer completely, restore the original, delete the application, and delete `~/Library/Application Support/InvokersRu`.

## Risks and limitations

- The translation is machine-generated. Wording may be inaccurate, particularly in mechanics. Automated checks preserve literal numbers, percentages, durations, placeholders, and rich-text structure, but they cannot prove semantic quality.
- Some store, promotion, and event text is rendered from the server and remains Ukrainian. A client-side localization file cannot change it.
- This project is unofficial and is not affiliated with HitZone Inc. Current game rules require caution around client changes; use it at your own risk.
- Exactly one runtime-cache file is replaced:
  - Windows: `%USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n\dl_uk_UA.bin`
  - macOS: `~/Library/Containers/<UUID>/Data/Documents/i18n/dl_uk_UA.bin`
  - Android: `/sdcard/Android/data/hitzone.anima.spirit.guardians/files/i18n/dl_uk_UA.bin`
- The signed game/application package, executable code, and protection are not modified. The localization cache is outside the signed package.
- macOS needs Full Disk Access to read the game's app container. Android debugging over USB or Wi-Fi should be turned off again after installation.

See [NOTICE.md](NOTICE.md) for the legal notice.

## Troubleshooting

The macOS log is at `~/Library/Application Support/InvokersRu/patcher.log`.

| Symptom | Cause and resolution |
| --- | --- |
| “Could not create backup” on Mac | Disk access is missing. Grant it in settings; if the switch is already enabled, remove the row with `−` and add the app again. |
| “Ukrainian text has not been downloaded” | Ukrainian was not selected or the file has not finished downloading. |
| Localization disappeared | The language selector was opened or the game updated. Install the localization again. |
| “Could not build localization for this game version” | The game updated and no exact adapted profile exists yet. |
| Android phone is not listed | Debugging is disabled, the phone authorization was not accepted, Samsung Auto Blocker is enabled, or a USB driver is missing. Try Wi-Fi. |
| “Text file was already replaced by another tool” on Android | A different method changed the file and this tool has no matching original. Use option 3 so the game downloads an official copy. |
| “Phone did not respond within five minutes” | The phone screen was locked. Unlock it and retry. |

---

## For developers

The cross-platform CLI targets .NET 10 and has no external runtime dependencies:

```sh
dotnet build src/InvokersRu.Cli/InvokersRu.Cli.csproj -c Release
dotnet run --project src/InvokersRu.SmokeTests -c Release --no-build -- PATH_TO_PRIVATE_FIXTURES
```

Platform builds:

```sh
./scripts/build-mac-app.sh
./scripts/build-android-apk.sh
./scripts/build-pc-helper.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-windows-preview.ps1
```

The macOS build calculates the exact patcher-driver SHA-256 and writes it into `config/mac-patcher.json`. The Android signing key lives outside the repository at `~/.config/invokersru/android-release.keystore`; losing it prevents Android updates under the same application identity.

### How translation works

The game reads current localization tables from a user-writable runtime cache rather than only from its signed application bundle. The project replaces only the existing Ukrainian cache slot.

Public `translations/ru_RU.jsonl` is a source-free JSON Lines overlay. It contains key ids, source hashes, and Russian output, but no English/Ukrainian plaintext:

```json
{"id":"16_HEX","source_sha256":"64_HEX","translation":"Russian text","status":"draft"}
```

`source_sha256` automatically makes a row stale when its authoritative English source changes. Missing/stale/rejected rows become exact English fallback. The LOC1 writer preserves the base slot's header, locale identity, schema, and ordered keyset, rebuilds data length, and reparses its output.

On macOS the app's stable Mach-O launcher retains the Full Disk Access identity while its downloaded driver is stored at `~/Library/Application Support/InvokersRu/runtime/`. Modifying the signed app bundle itself would change its identity and revoke that permission.

### Main CLI commands

```sh
InvokersRu.Cli inspect FILE
InvokersRu.Cli roundtrip FILE
InvokersRu.Cli cache-status
InvokersRu.Cli cache-profile --output FILE
InvokersRu.Cli jobs --english EN --output JOBS
InvokersRu.Cli import-results --english EN --jobs JOBS --results RES --output NEXT
InvokersRu.Cli validate --english EN --translations CAT --include-draft
InvokersRu.Cli build --english EN --base UK --translations CAT --output OUT --include-draft --raw
InvokersRu.Cli update-status --json
InvokersRu.Cli update-refresh --json
```

See [docs/](docs/) for architecture, platform details, release safety, update-channel design, and the translation workflow.

### Supporting a new game version

1. Capture a blocked profile into an ignored workspace: `cache-profile --output work/profiles/runtime-cache-profile.<version>.json`.
2. Diff by `source_sha256`; unchanged translations remain valid, changed rows become stale, and new ids go to deterministic jobs.
3. Translate/review the changed jobs and import them.
4. Reproduce the exact LOC1, catalog hash, output hash, keyset, and composition counts.
5. Publish a new signed compatibility/data release through the controlled release-key workflow. Never edit a hash merely to accept a new client.

Original game files (`*.bin`, `*.bin.br`, and `manifest.dat`) must never enter the repository or a public release.

### Rebuild, audit, and other languages

- [Windows self-build and verification — English](docs/windows-self-build.en.md)
- [Самостоятельная сборка Windows — русский](docs/windows-self-build.ru.md)
- [Community localization kit — create another language offline](community-localization-kit/README.md)
