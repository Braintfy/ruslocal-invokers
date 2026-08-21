# Invokers: Titan Legacy Russian localization

[Русская версия](README.md)

An unofficial Russian translation of the game. **41,037 of 41,292 strings** are translated: menus, quests, skill descriptions, items, and battle messages. One string remains in English and another 254 entries are empty in the game itself.

A model did the translating and no human editor has been over the text. After the first pass the corpus was corrected against screenshots from the game: one glossary instead of five names for the same effect, a proofreading pass over prose, and shortening the labels that did not fit the interface. Numbers, percentages and durations are checked against the source automatically.

This project does not distribute original game files: Russian text is composed on each user's device from that user's own installed game data.

## Project numbers

| | |
| --- | --- |
| Strings in the game | 41,292 |
| Translated | **41,037** — 99.4%; one internal string and 254 entries empty in the game itself |
| Volume of Russian text | 7.0 million characters |
| Passes over the corpus | 7 — first translation, rework on a stronger model, repair of half-English rows, prose proofreading, name unification, screenshot QA, interface labels |
| Models | claude-haiku-4-5 — 25,812 rows, claude-opus-5 — 13,702, gpt-5.6 — 1,523 |
| Tokens for one full pass | ≈3.7M in, ≈2.8M out — measured by the `jobs` command |
| Tokens for translation | ≈18M: a full run over the corpus (~10M), reworking a quarter of the rows on a stronger model (~2M), five targeted passes (~6M) |
| Tokens for building the PC version | ≈10M, through ChatGPT |
| Total | ≈28M tokens |
| Elapsed | 6 days, 16–21 August 2026 |
| Automated checks | 22 classes of error: placeholders, numbers, units, markup, braces, Cyrillic look-alikes of Latin abbreviations |
| Code | 18,500 lines of C# and 1,175 of Java, no external dependencies |
| Platforms | Windows, macOS, Android, emulators |

Rows, characters, models and passes are read out of `translations/ru_RU.jsonl` itself; the per-pass token figure is the `jobs` estimate. The totals are an order of magnitude rather than a billing statement: on long strings full of markup the fixed per-agent overhead costs more than the strings do, which is why splitting the work finer gets more expensive, not less.

## Translate into your own language

The project translates into Russian, but nothing in the pipeline is tied to that language. If the game already carries a slot for yours — and it carries fourteen — the same route works, and no game file leaves your machine.

| What you need | Where it is |
| --- | --- |
| The whole pipeline, step by step | **[Community localization kit](community-localization-kit/README.md)** — from capturing a tuple of your own game build to a Windows installer and a macOS image |
| Prompts for the model | [translation](community-localization-kit/prompts/translation.md), [review](community-localization-kit/prompts/review.md) |
| Prompts proven on the full corpus | [first translation](prompts/translation-system.ru-v2.md), [repairing half-English rows](prompts/translation-repair.ru-v4.md), [prose proofreading](prompts/translation-polish.ru-v4.md) |
| Templates | [glossary](community-localization-kit/templates/glossary.example.json), [style guide](community-localization-kit/templates/style-guide.example.md), [language config](community-localization-kit/templates/language-config.example.json) |
| How it works underneath | [LOC1 format and pipeline](docs/architecture.md), [translation workflow](docs/translation-workflow.md) |

What the pipeline cannot do: it does not add new languages to the game and does not change its format. It works only with a slot the game already has and the current LOC1; strings absent from your build stay English.

The lessons from this project that will save you time are in [docs/translation-workflow.md](docs/translation-workflow.md): why long strings with markup need a stronger model, how terminology drift between batches is detected, and which three classes of defect are visible only by looking at screenshots.

## Download

Everything is on the [releases page](https://github.com/Braintfy/ruslocal-invokers/releases/latest).

| Where you play | What to download |
| --- | --- |
| Windows PC | `InvokersRu-…-win-x64.exe` — installer |
| Mac | `Rusifikator-Invokers-….dmg` — disk image |
| Android phone, no root | `Rusifikator-Invokers-ANDROID.zip` — a computer helper; the phone app is inside |
| Android phone with root | `Rusifikator-Invokers-Android-….apk` — the app alone |

The Mac version can update itself. The Android computer helper fetches the current translation from the project. Windows 3.1.1 ships an offline bootstrap translation and can fetch newer signed translation data and optional exact game profiles from the fixed GitHub channel without replacing the EXE. When an exact profile is unavailable, it can build a byte-pinned local compatible-revision profile for a supported LOC1 family. Only rows whose English source and Ukrainian hint both match exactly are translated; new or changed rows remain English.

The 3.1.1 Windows installer accepts x64 Windows 10 build 14393 (version 1607,
including Enterprise 2016 LTSC) and newer, plus Windows 11. It is self-contained,
so players do not install .NET separately. Microsoft's .NET 10 support on
Windows 10 is limited to maintained Enterprise/LTSC branches; running on an
out-of-support edition does not extend that edition's lifecycle. The preview is
not Authenticode-signed yet; Windows can show SmartScreen or `Unknown publisher`,
so compare its SHA-256 with the GitHub Release.

The Mac game is the iPhone/iPad application. The App Store installs those applications only on Apple Silicon Macs (M1 or newer), so the game cannot be installed on an Intel Mac. The localizer itself is universal: on Intel it explains this limitation, and it can still help install the localization on a connected Android phone.

## Android installation

Since Android 11 one application cannot modify another's data. Root is the only way to do it entirely on the device; without root a computer transports the files, while the phone still performs the build. Nothing has to be installed by hand — the helper downloads what it needs.

**In every method**, first select **Ukrainian** in the game, wait for the text to download, and fully close the game. Russian text occupies the Ukrainian slot, the only Cyrillic one the game has.

### Method 1 — device only (root required)

Install the APK, open “Русификатор Invokers,” choose Install translation, and grant root access.

### Method 2 — an emulator on this computer (BlueStacks and similar)

The shortest route of the four: the emulator runs on the same machine, so there is no cable, no driver, no pairing code and no “Allow USB debugging?” prompt on someone else's screen. The helper scans the local ADB ports itself and reads the BlueStacks configuration.

With root enabled in the emulator the computer is not needed at all: drop the APK onto the emulator window and follow method 1. Root is switched on under Settings → Advanced → Root access, after which the emulator has to restart.

Without root:

1. BlueStacks: Settings → Advanced → **Android Debug Bridge** — enable it and press Save. The same screen shows a line like `127.0.0.1:5555`.
2. Extract `Rusifikator-Invokers-ANDROID.zip` and run **`Русификатор-Android.cmd`**.
3. Choose **“an emulator on this computer”**, then Install translation.

If the emulator is not found the helper asks for the port — take it from that same line. With several emulator windows open, leave one running: the helper takes the first that answers.

Verified against a stub reproducing an emulator's responses; not tested against a live BlueStacks.

### Methods 3 and 4 — a phone over Wi-Fi or USB

1. Extract `Rusifikator-Invokers-ANDROID.zip` and run **`Русификатор-Android.cmd`** on Windows or **`Русификатор-Android.command`** on Mac (first launch: right-click → Open).
2. Enable Developer options: Settings → About phone → Software information, tap Build number seven times.
3. Under Developer options enable **Wireless debugging** or **USB debugging**.
4. Pick the matching item in the helper and choose Install translation.

Over Wi-Fi the phone and computer must share a network; the helper asks for the address and code from Wireless debugging → Pair device with pairing code, and the code is short-lived. On Windows this is more reliable than a cable because it needs no driver.

Over USB you need a data-capable cable and “Allow USB debugging?” accepted on the phone. On **Samsung** disable **Auto Blocker** first; on **Xiaomi** also enable “USB debugging (Security settings).”

### Restore the original on Android

In the same helper, option 2 restores its saved copy and option 3 removes the language data so the game downloads an official copy again. The terminal equivalent is `scripts/android-patch.sh` with `status`, `apply`, `restore`, and `reset`.

See [docs/android-client.md](docs/android-client.md) for the technical rationale.

## Windows PC installation

This path is for a game installed on the PC itself. The patcher finds the standard localization cache, detects the observed client version and selects either a published exact profile or a locally pinned compatible-revision plan for a supported LOC1 family.

> **Before localization:** select **Ukrainian** in the game, wait for its text to download, then fully quit both the game and launcher, including the launcher's system-tray icon.

1. Download `InvokersRu-…-win-x64.exe` from the [latest release](https://github.com/Braintfy/ruslocal-invokers/releases/latest) and install it. Administrator rights are not required.
2. Open the game settings, select **Ukrainian**, and wait until the text finishes downloading. Russian uses the Ukrainian slot because it is the game's existing Cyrillic slot.
3. Fully close the game and launcher, including the launcher icon in the system tray.
4. Start **InvokersRu** from the Start menu, select **Check**, then **Install localization**.

The patcher shows the detected version, selected mode, translated and English-fallback counts, catalog provenance, and backup state. On a mismatch it names the component and its current and expected values — for example EN/UK content and revision — and distinguishes stale translation data from a structural boundary or inconsistent local state. A version number alone is not a blocker. Writes are blocked by a running game/launcher, untrusted or damaged data, an interrupted transaction, or an incompatible cache path, LOC1 schema, locale slot, content family, or key layout. There is no force or fuzzy-matching mode.

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

## Updates, and updating the game

### Windows 3.1.1 signed data channel

The Windows application checks one pinned GitHub data-channel URL. Translation catalogs and optional exact profiles are authenticated by a separate project ECDSA P-256 key. The patcher verifies the signature, monotonically increasing release sequence, expiry, size limits, download origin, and SHA-256 of every artifact before using it. It prefers an exact profile; otherwise a supported raw LOC1 schema-4 family can use a locally pinned compatible-revision plan with exact per-row EN+UK matching. A normal compatible data update therefore does not require a new EXE.

If you installed the old Windows 3.0 patcher or the already published 3.1.0
preview, replace it with 3.1.1 once. Version 3.0 does not implement the signed
data-channel protocol; 3.1.0 lacks the Windows 10 1607 fix and the new compatible
revision mode. After that migration, normal compatible translation updates do
not require another EXE. Offline, the authenticated embedded bootstrap can serve
an exact or structurally compatible revision; last-known-good data remains limited
by expiry and installed-state rules. The data signature is separate from
Authenticode, so an unsigned EXE can still show SmartScreen or `Unknown publisher`.

**macOS** fetches a newer translation and updateable driver by itself, accepting a download only when it matches the published checksum. Reinstalling the app and granting disk access again is normally unnecessary because that driver lives outside the bundle. A new DMG is needed only when the app says its fixed launcher changed; then Full Disk Access must be granted to the new build — remove the old row with `−`, press `+`, and add the app again.

Disable self-update with:

```sh
touch ~/Library/Application\ Support/InvokersRu/no-self-update
```

## After a game update

A game update usually restores the official language file. On Windows, select Ukrainian again, wait for the download, fully quit the game and launcher, and choose Check. The patcher prefers a published signed exact profile. If none exists but EN, UK, and the version stamp form a supported raw LOC1 schema-4 family with the expected locale slots and identical ordered keys, it builds a locally pinned compatible-revision plan and offers Update localization. Only exact `source_sha256` + `hint_sha256` matches become Russian; new, changed, or missing rows remain English and null/service entries keep their base value. A new EXE is needed only when the path, schema, locale slot, content GUID family, layout, or trust boundary changes.

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
| “Could not build localization for this game version” | The tuple is outside the supported LOC1 family, no exact source+hint row remains usable, the selected catalog is unavailable, or the patcher itself is too old. Update Ukrainian data, fully close the game and launcher, then choose Check again. |
| Android phone is not listed | Debugging is disabled, the phone authorization was not accepted, Samsung Auto Blocker is enabled, or a USB driver is missing. Try Wi-Fi. |
| “Text file was already replaced by another tool” on Android | A different method changed the file and this tool has no matching original. Use option 3 so the game downloads an official copy. |
| “Phone did not respond within five minutes” | The phone screen was locked. Unlock it and retry. |

---

## For developers

The cross-platform CLI targets .NET 10 and has no external runtime dependencies.

The SDK version is pinned in `global.json` (10.0.302, `rollForward: latestPatch`). A different build makes `dotnet` refuse to compile — install the pinned one alongside whatever you already have:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.302
```

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
