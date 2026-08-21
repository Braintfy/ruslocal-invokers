# Changelog

## Причина отказа — 2026-08-21

- made the macOS driver name which table moved instead of guessing. It used to answer every build failure with «скорее всего игра обновилась», which is a guess that happens to be wrong in the case that actually occurs: today the Ukrainian table changed and the build succeeded, while a changed English table is the one that genuinely strands rows. It now reads both content versions out of the game's own files and prints the builder's real error alongside them;
- explained the result of a successful build too. A build can finish and still leave English on screen, because rows whose English was rewritten no longer match anything; the driver now reports how many stayed English and why, rather than letting a player discover it mid-fight and conclude the patcher is broken;
- gave the Android helpers the same answer with what they have. There is no LOC1 parser on the computer side there, so the count the phone reports is compared against the catalog it was handed — a shortfall means the same thing — and a failure from the phone now carries the game's version stamp with it.

## Профиль Prod_0.60.0_71 — 2026-08-21

- certified a profile for the Ukrainian table the game quietly replaced. The client version did not move — still 0.60.1247 — and neither did the English table, but the Ukrainian base went from `Prod_0.60.0_68` to `Prod_0.60.0_71`, 489 bytes larger. That is why the translation vanished and why the Windows patcher refused with `REFUSE_UNKNOWN_OR_INCONSISTENT`: its checksums are of the base, and the base changed underneath it;
- confirmed the translation itself survived intact before certifying anything. The catalog keys off the English `source_sha256`, English did not change, and a rebuild against the new base still applies all 41 037 rows with the same 1 English and 254 base fallbacks. The 5 356 new warnings are `stale-hint`: the Ukrainian context a row was translated beside has changed, which is advice to re-read, not a defect;
- kept both previous outputs in `superseded_artifacts`, so a player who installed before this update is still recognised and can still restore their original instead of being locked out by an unknown hash.

## Каталог — 2026-08-21

- gave `Fate Star` and `Destiny Star` different names. Both had been translated as «Звезда судьбы», which is how the guarantee for the pair came out reading «Гарантия звезды судьбы и судьбы» — two distinct items collapsed into one word. Fate is «рок» now;
- stopped one orb wearing another's name: `Wonder Orb Guarantee` was labelled «Гарантия Чудо-сферы», and Чудо-сфера is `Miracle Orb`, a different item on the same screen;
- settled the orb families that carried three names each. `Miracle Orb` appeared as «Чудо-сфера», «волшебная сфера» and «Чудо-Шар»; `Dream Orb` as «Сфера снов» and «Сфера Мечты». Derived rows — bonus, fragment, guarantee — now follow the item they belong to;
- normalised capitalisation inside compound item names to the glossary rule, so «Гарантия Сияющей Звезды» matches «Сияющая звезда» rather than shouting at it;
- all of it lives in `terminology.ru.json`, so the next pass cannot drift it apart again.

## README — 2026-08-21

- put the two questions people actually arrive with at the top of the README: what this cost, and how to do it for another language. The community kit and the prompts were already in the repository, buried at the very bottom under «Для разработчиков» where nobody looking for them would go;
- built the numbers block out of things that can be checked rather than remembered — line counts, characters, models and passes come from the catalog itself, the per-pass token figure from the `jobs` estimate. The totals are stated as an order of magnitude, because that is what they are;
- corrected the download table, which named files that do not exist in the release: `Rusifikator-Invokers-PC.zip` is published as `Rusifikator-Invokers-ANDROID.zip`, and the APK carries its version in the name;
- compressed the Android section from four near-identical walkthroughs to four short ones with the shared step — pick Ukrainian, close the game — stated once, and merged the two update sections that had been explaining the same thing twice;
- brought README.en.md level with the Russian one, which had drifted three changes behind: it still had no emulator method, no numbers, no pointer to the localization kit, and named release files that do not exist;
- documented the SDK pin in the developer section. `global.json` requires 10.0.302 and a newer SDK simply refuses to build, which is a confusing first five minutes for anyone cloning the repository; the two update-channel commands were missing from the command list as well.
## Windows 3.1.1 Preview — 2026-08-21

- lowered the installer floor from Windows 10 1809 to x64 Windows 10 build
  14393 (version 1607, including Enterprise 2016 LTSC), while retaining a
  self-contained `win-x64` payload that does not require a system .NET Runtime;
- used the oldest Windows API target pack available in the pinned .NET 10 SDK
  (17763) while declaring the actual supported-platform floor as 14393, so
  neither the application metadata nor Setup accidentally requires Windows 11;
- made Setup, uninstall, and PE product metadata derive from the exact
  `AppVersion`, and added packaging regressions for the OS floor, x64 mode,
  target framework, and 3.1.1 version defaults;
- added the bounded `compatible-revision` runtime mode: a new game/content
  revision no longer needs a new EXE or exact profile when it still uses the
  trusted raw LOC1 schema-4 family, EN=1/UK=8 slots and identical ordered keys;
  only rows with exact current English source and Ukrainian hint hashes are
  applied, while changed/new/missing rows remain English and null entries keep
  their base value;
- made exact and compatible profiles interoperable across game updates,
  catalog-only updates, launcher repair and interrupted transactions. Dynamic
  profiles pin the observed tuple, catalog, locally materialized output and
  composition counts before the existing locked, journalled atomic mutation;
  exact state/backup/history authentication remains required for restore and
  recovery, with no force or fuzzy-matching path;
- kept the community preview honestly unsigned. Windows may still show Unknown
  publisher or SmartScreen; users of the already published 3.1.0 preview need
  this one-time player update before later compatible data-only updates.

## Windows 3.1.0 Preview — 2026-08-21

- replaced the retired CMD/PowerShell installer with a self-contained WinForms patcher that discovers the fixed runtime cache, identifies the exact game/content version, reports coverage and English fallbacks, and exposes only guarded install, update, recovery, and exact restore actions;
- routed every game-cache mutation through the Core transaction: fixed paths, process and reparse checks, immutable hash-named backup, journalled atomic replacement, preimage revalidation, rollback, and fail-closed recovery;
- added a pinned ECDSA P-256 GitHub data channel. Future compatible translation catalogs and exact game profiles can update without replacing the EXE; the patcher separately blocks data that requires a newer patcher or a changed LOC1/path/slot implementation;
- added strict update-envelope, URL, size, expiry, anti-rollback, last-known-good, exact profile, catalog, composition, and output checks, including offline fallback and historical restore/recovery;
- added reproducible Windows payload and Inno Setup tooling, bilingual self-build verification guides, and an English community localization kit. The current preview is intentionally not Authenticode-signed, so Windows may show Unknown publisher or SmartScreen until publisher signing is introduced.

## Эмуляторы и скобки — 2026-08-20

- added the emulator route to the Android helper. An emulator runs on the same computer, so there is no cable, no driver, no pairing code and no prompt on someone else's screen to accept — it is the shortest path of the four. The helper scans the local adb ports the common emulators ship with and, on Windows, reads the port of every BlueStacks instance out of `bluestacks.conf` instead of guessing;
- fixed what actually stopped emulators from working: the readiness check demanded the shell belong to the `ext_data_rw` group. That group arrived with scoped storage, and emulators usually run an older Android where it does not exist — so a device was turned away at the door while its shell could write to the game directory perfectly well. The check now creates and removes a probe file instead, which is the true question on every Android and still catches the corporate policies and Secure Folder clones the group check was meant for;
- caught a class of defect the validator could not see. Fourteen quest strings carried doubled braces — `{{0}}` where the game wanted `{0}` — and the protected-token regex refuses to look inside braces, so `{{0}}` and `{0}` both extracted as the same token and the multiset matched. The game printed the braces literally. Counting brace characters is what separates the two, and that is now an error;
- repaired those fourteen strings, and their wording along with the braces: «Победите Fire Garuda этап {0}» read as an instruction to defeat a stage;
- rebuilt the macOS image with the SDK the repository pins, and the helper archive so the emulator route and its instructions actually reach players.

## Перевод — 2026-08-20

- shortened the stat labels that were covering their own numbers. The stats panel draws the label and the value in fixed columns, and «СКОРОСТЬ ДВИЖЕНИЯ» at 17 characters simply ran under the figure; «КРИТ. ШАНС» at 10 sits clear, so that is the width the panel actually has. Combo, Skill and Move speed, Mana Gen and Skill Recovery now fit, the worst case dropping from 17 to 12;
- settled the same stats across their spellings while doing it. The catalog held `Crit DMG`, `CRIT DMG` and `Crit Damage` as three different Russian phrases, and `Combo SPD` as both «SPD комбо» and «Скорость комбо» — the panel picks whichever id that screen uses, so a label could read one way in one place and another elsewhere;
- moved the override pass last in the normaliser. An exact-string override is a decision about one specific label, and the trailing-period heuristic was trimming the dot off «СКОР. ДВИЖ.» after the fact;
- checked the Ukrainian duration in battle («23 г. 55 хв.») against the game's own file: there is no such string in it, and no hour or minute unit anywhere in the corpus. The client formats durations from the active locale rather than the translation table, so no catalog can change it;
- rewrote what the README claims about the translation. It still said the text was raw machine output, which stopped being the whole story after the terminology pass, the prose proofread and this label work.

## ПК 1.0.0 — 2026-08-20

- added a tool that translates the PC build of the game: unzip, run, pick install. The CLI already knew how to compose the file and the game's own two language files are right there on the machine, so nothing was missing except a driver to put them together. `scripts/build-pc-game-tool.sh` cross-builds the win-x64 binary from macOS and packs it with the catalog;
- shipped the catalog inside the archive instead of downloading it each run. It is 35 MB, and that transfer turned out to be the least reliable step in the whole flow — `Invoke-WebRequest` aborted at 22 KB with "the response ended prematurely" in every test. A newer catalog is still fetched when the network allows, so the bundled copy is the floor rather than the ceiling, and the tool works offline on the first try;
- found the game by content rather than by path. The folder is located by looking for `dl_en_US.bin` under LocalLow, two levels down, so renaming the studio or the product in a future build does not break it; zero, one and several matches each get their own message, and several is refused rather than guessed;
- kept the original outside the archive, under `%LOCALAPPDATA%\InvokersRu`, addressed by its hash. Deleting the archive or unpacking it elsewhere no longer strands anyone with a patched file and no way home, and a reinstall over an already-translated file will not overwrite the saved original with a patched one;
- verified the result against the macOS driver rather than by eye: from the same untouched Ukrainian file both compose a byte-identical output, `4E3AC2FB…`. Restore returns the exact original. Two independent implementations agreeing to the byte is a stronger check than any assertion about the contents;
- fixed the one defect the tests caught: the CLI refuses to overwrite its report file, and the driver deleted only the built file before a build, so a second run in a row failed with nothing on screen to explain it;
- wrote down what could not be checked without a Windows machine that has the game — the real data path, whether the PC build's files match the Mac ones, and Windows PowerShell 5.1 — with the commands to settle each, in `docs/pc-client.md`.

## macOS 2.3.0 — 2026-08-20

- built the bundle universal. Both slices were arm64 only, so on an Intel Mac the app did not fail — it never started: Finder drew a prohibitory badge on the icon and there was nowhere for a message to appear. Rosetta does not help here, it translates the other direction. The launcher is now compiled for both architectures and the CLI is two publishes joined with `lipo`, which is why the image grew from 29 MB to 60 MB;
- replaced the one dialog that covered every empty result. It blamed Full Disk Access for all of them, which is wrong in the common cases: on a machine that cannot run the game no permission will ever help, and a game that has never been launched has no data to protect. There is now a separate message for each real cause — Intel hardware, game not installed, game never launched, access not granted, several containers — and the Intel one says plainly that Invokers for Mac is an iPhone app and App Store installs those only on Apple Silicon;
- moved the Android check ahead of the Mac data check. A computer that cannot run the game itself can still build the file and carry it to a connected phone, and dying on the missing Mac data first hid that from exactly the users with no other route;
- separated the bundle being built from the oldest bundle that still works. They were the same value, so every rebuild told every existing user to download the image again; the nag now also compares dotted numbers instead of strings, which had reported a newer bundle as outdated;
- fixed `find_cache_root` returning failure after succeeding. It piped into `head`, which closes the pipe early, and under `pipefail` that became a non-zero exit — masked at the only call site by `|| true`, and wrong for anyone else who checked;
- lowered the minimum to macOS 12 and refreshed the counts, which still advertised 40 541 strings from before the screenshot QA pass;
- stopped self-update from walking backwards. It replaced the driver whenever the published version merely *differed* from the running one, so a freshly released image whose changes had not reached `main` yet would downgrade itself to the older script on its very first launch and quietly undo the release. It now refuses to move to an older version unless the manifest says `allow_downgrade`, which keeps a deliberate rollback possible without letting an accidental one through.

## Android 2.0.0 — 2026-08-20

- turned the phone into the builder and the computer into a courier, which is what finally makes the translation installable without root. The app already knew how to compose the LOC1 file; what it cannot do is reach the game's directory. So the computer copies the game's two language files into the patcher's own directory — the one place adb may write and the app may read without any permission — starts the build with an explicit intent, and copies the result back. The computer needs adb and nothing else: no .NET, no repository, no build tools;
- shipped that computer side as `Rusifikator-Invokers-PC.zip`, 44 KB, with the APK inside it. On first run it downloads platform-tools from Google itself, so the player installs nothing by hand. `Русификатор-Android.cmd` for Windows, `Русификатор-Android.command` for macOS, both leading through a three-item menu: install, restore, or make the game re-download the original;
- added the wireless route. Android's own Wi-Fi debugging carries adb with no cable at all, and on Windows it is the more reliable of the two because it needs no USB driver. The script walks through pairing, including the detail that trips everyone: the port in the pairing dialog is not the port to connect to;
- rebuilt the app as a wizard. It works out what the phone can actually do — root, files waiting from a computer, or neither — and shows one obvious button for that case. The two routes it cannot take itself are written out as numbered steps with buttons that open the settings screens, including how to reveal developer options in the first place;
- moved the backup and the record of what was installed onto the phone. A player who re-downloads the archive, or switches computers, can still put the original text back instead of being stranded with a patched file and no way home;
- fixed the catalog reader, which had never been exercised against the real file: it looked for `"id":"` and the catalog is written as `"id": "`, so the app applied zero translations and produced a file that was purely English. Verified now against the published catalog, all 41 037 strings;
- cut the on-device build from minutes to two seconds. `String.format("%02X", …)` ran 2.6 million times over the catalog; with hand-written hex and a single pass instead of three, composing the file is no longer the slow part;
- checked the Java build against the .NET one: same 41 292 entries with the same values. The bytes differ only because the strings land in the pool in a different order, and the offsets are explicit, so the game cannot tell;
- exported JAVA_HOME in the APK build script, which had been relying on whatever the calling shell happened to have on PATH.

## Android APK 1.0.0 — 2026-08-20

- added an Android APK that installs the translation on the phone itself where root is available, and otherwise explains exactly what to do. It composes the Russian file on the device: the LOC1 container and the catalog reader are reimplemented in Java, because the .NET tool cannot run there;
- kept the app honest about the limit rather than failing mysteriously. Since Android 11 an ordinary app cannot read or write another package's Android/data — the Storage Access Framework refuses that path and MANAGE_EXTERNAL_STORAGE does not cover it — so without root the app states this, walks through enabling developer mode including the seven taps on the build number that reveal it, and opens both the settings screen and the guide;
- built the APK without Gradle, driving aapt2, javac, d8 and apksigner directly, so nothing is pulled from Maven and the package contains no third-party code;
- kept the signing key outside the repository and reused it across builds. The first build generated a fresh key every time, which Android rejects as an incompatible update and would have stranded anyone who had already installed the app.

## Разведка Android — 2026-08-20

- проверено на Samsung Galaxy S25 (Android 16) с игрой 0.60.1247: рантайм-кэш лежит во внешнем каталоге `/sdcard/Android/data/hitzone.anima.spirit.guardians/files/i18n/`, и `dl_en_US.bin` с `dl_uk_UA.bin` **побайтово равны** снятым с macOS той же версии, так что собранный для macOS русский файл встал на Android без пересборки: 41 037 строк применено, 1 английский fallback;
- подтверждено, что доступ через ADB реально работает: у `shell` есть группы `ext_data_rw`/`ext_obb_rw`, проверены push, чтение, `mv` и `rm` — включая переименование, которое на части свежих прошивок ломается;
- зафиксировано, что приложением на самом телефоне обойтись нельзя: с Android 11 чужой `Android/data` закрыт и для SAF, и для `MANAGE_EXTERNAL_STORAGE`, внутренний каталог закрыт изоляцией по UID, а `run-as` на релизной сборке отказывает — проверено;
- разгадана хэш-функция ключей LOC1: это FNV-1a 64-bit от UTF-8 строкового ключа. Сопоставление ключей из IL2CPP-метаданных со значениями корпуса сходится смыслом (`ui-multibattle-exit-popup-title` → «Stop Multi-Battle?»), что снимает главное белое пятно формата и даёт контекст экрана по префиксу ключа;
- отмечено, что в сборке 0.59.1005 тот же каталог использовал другой контейнер `cache.dat2` с магией `AMNA` и текстовыми ключами; в 0.60.1247 игра перешла на общий для платформ LOC1, поэтому отдельный парсер под него не нужен.

## 2.3.0-macos — 2026-08-19

- restored the Latin stat abbreviations the model had transliterated: `ATK` was written as Cyrillic `АТК` in 866 places, which is most damage formulas in the game, plus `AoE` as `АоЕ`/`АоЭ` in five hints. The replacement only applies where the English source actually contains the abbreviation;
- settled 89 terms that had drifted into two spellings each and applied the choice to 123 records, leaving zero terms with more than one rendering. `BACK` was showing as «СПИСОК» in one place and is now «НАЗАД» everywhere; `Fusion Limit` lost its stray capital, `Bio` became «Биография»;
- unglued five internal keys the bulk pass had translated into run-together CamelCase — «ДебаффОтклоненаЗащита» now reads «Дебафф: игнорирование защиты», matching the sibling key that was already correct;
- shipped all of this as a driver update, so it reaches players without a new disk image: the bundle version stays 2.2.0 and only the script and the overlay move.

## 2.2.0-macos — 2026-08-19

- made the patcher update itself from the repository, so the application no longer has to be reinstalled for every fix. macOS pins a Full Disk Access grant to the bundle's code signature, and re-signing after touching any sealed file changes the cdhash and silently revokes the grant — verified directly: editing a resource reports «a sealed resource is missing or invalid», and re-signing moved the cdhash from 58b93a5b to e0484b8c;
- moved the driver out of the bundle to `~/Library/Application Support/InvokersRu/runtime/` and had the launcher prefer it, leaving the bundle byte-identical for its whole life so the grant survives every update. The launcher passes the bundle's Resources path in the environment, because the driver can no longer locate the CLI relative to itself;
- accepted a downloaded driver only when its SHA-256 matches the manifest and it passes a syntax check, and let anyone opt out with `~/Library/Application Support/InvokersRu/no-self-update`;
- split the version in two: the driver version changes freely, while `minimum_bundle_version` marks the rare release that genuinely needs a new download, and that dialog now explains the grant has to be recreated;
- had the build compute the driver checksum, write it into the manifest and delete superseded disk images, so a stale hash or an old build cannot be published by accident;
- rewrote the README and the in-image instructions around what a player actually does, with download links per platform.

## 1.1.0-macos — 2026-08-19

- reworked 8 484 unique strings with a stronger model after screenshots showed half-English skill descriptions in game. The bulk pass had left 9 227 records partly or wholly untranslated: on long strings dense with placeholders and rich-text tags it preserved the markup and translated only isolated words, producing text like «This Инвокер is at the maximum Level … Ascend them к 6 … к keep Levelling Up»;
- measured the result the same way it was detected: partly-English records fell from 5 507 to 2 and wholly-English ones from 3 720 to 662, with the validator reporting zero errors across all 40 541 records;
- pinned skill phrasing and buff names in the glossary against the terminology published on invokersdb.app and ggnoluck.com, so «Casts a [X] debuff on each target hit, lasting Ns» now reads «Накладывает дебафф [X] на каждую поражённую цель на Ns» everywhere, and bracketed effects are translated consistently: [Stun] as [Оглушение], [X% ATK Up] as [Усиление ATK X%];
- kept a plus or minus sign out of translated effect names, because the numeric check compares tokens against the source byte for byte and «+40%» would be refused where the source says «40%».

## 1.0.0-macos — 2026-08-19

- made the bundle's main executable a real Mach-O binary (`mac/launcher.c`) that runs the driver script as a child. A bundle whose entry point is a shell script cannot hold a Full Disk Access grant: the running process is `/bin/bash`, a platform binary the grant cannot be pinned to, so the switch in System Settings showed as enabled while every read of the game container still failed with EPERM. Directory listings kept working, which is why the app found the cache root and then could not back it up;
- explained the stale-grant case, which looks identical from the user's side: macOS binds a grant to the exact application it was created for, so after the app is replaced the old entry keeps an enabled switch and grants nothing until it is removed with «−» and added again with «+».

## 0.6.0-dev — 2026-08-19

- continued the install automatically the moment Full Disk Access is granted: the waiting dialog is dismissed by a watcher that polls for the grant, so the switch in System Settings is the last thing the user touches;
- offered a self-relaunch when the grant only reaches a restarted process, and skipped the introduction on that second launch so nobody answers the same questions twice;
- replaced the terse success message with the guidance that actually prevents the common failure: the language picker must not be opened, part of the text stays English on purpose, names stay Latin, and an update means installing again; the same dialog can launch the game;
- refused an overlay that lost more than half its records, which keeps a working installation from silently reverting to a mostly-English one when the published catalog is stale or truncated.

## 0.5.0-dev — 2026-08-19

- translated the rest of the corpus: the public overlay now carries 40 541 records against `Prod_0.60.0_68`, so a build applies 40 541 of 41 292 strings and leaves 497 in English plus 254 empty source entries;
- ran the bulk pass as 158 batches on a small model at low reasoning effort, then filtered the output through the real importer, which refused 331 jobs on placeholder, number or unit mismatches; those stay English rather than shipping broken text;
- added `scripts/normalize-model-results.py`, which restores compact mechanic units the model transliterated (`6м` back to `6m`) and rebuilds each row from exactly the schema's fields; a unit is only ever restored when the English source actually contains it;
- stopped excluding needs-review records when the macOS app builds: two thirds of the catalog carries that flag only because identical English appears on several screens, and excluding it applied 576 strings instead of 40 541;
- fixed the terms the main menu shows every session and pinned them in the glossary: Fusion is Слияние, plus Index, Market and Event;
- requested the overlay with transfer compression, since it is now tens of megabytes of JSONL.

## 0.4.0-dev — 2026-08-19

- added a macOS application bundle so the preview installs by downloading one file and pressing a button: `mac/patcher-main.sh` drives native dialogs, `scripts/build-mac-app.sh` assembles the signed-ad-hoc `.app` and a `.dmg` around a self-contained `osx-arm64` build of the CLI;
- shipped no game data in the bundle: the Russian file is composed on the user's own machine from their installed game plus the public source-free overlay, which the app downloads from the repository at install time, so a new translation needs no new download of the app;
- checked `config/mac-patcher.json` for a newer app version on launch and degraded silently when offline or when the manifest is absent;
- warned explicitly about the behaviour that silently undoes the patch: the client re-downloads the localization file whenever a language is selected in the game, so the language must stay Ukrainian after installing;
- kept the same safety properties as the shell script: refuses to run while the game is open, verifies a content-addressed backup before and after, replaces atomically, re-verifies the installed hash and rolls back if it disagrees.

## 0.3.1-dev — 2026-08-19

- treated a stale source or stale Ukrainian hint as a preview warning instead of a blocking error, so a preview still assembles the records that survived a content update while a release keeps refusing them; a stale record is never composed either way, because the official English text is kept for it;
- added `scripts/mac-preview-patch.sh` with `status`, `apply` and `restore`: it refuses to run while the game is open, keeps a verified content-addressed backup of the original `dl_uk_UA.bin`, replaces the file atomically inside the writable container, re-verifies the result, and is idempotent in both directions;
- documented the locale slot table and why `uk_UA` stays the right target, together with the concrete glyph risk to look for on first launch.

## 0.3.0-dev — 2026-08-19

- added `cache-profile`, which snapshots a local runtime cache tuple into a schema-1 compatibility profile with real hashes, content versions and revisions, always as `readiness=blocked` / `certified=false`;
- stopped pinning one observed content release inside the profile parser: `en_US` locale 1 and `uk_UA` locale 8 stay fixed game constants, while locale and release revisions now come from the profile, so a newer build no longer requires a parser edit;
- made the default runtime-cache root cross-platform and resolved once per process; on macOS the per-installation container is discovered by looking for `dl_en_US.bin`, and a missing, unreadable or ambiguous container now fails with the reason and a `--cache-root` hint instead of silently falling back to a directory that holds no cache;
- added `--cache-root` to the read-only `cache-status`, `cache-plan` and `cache-profile`; the write commands keep deriving their own pinned root;
- refused a version stamp that is not a bare version string, so a padded or corrupted marker cannot become a profile identity;
- kept every installation-write gate unchanged: capability stays compile-time disabled in ordinary builds, mutation root and state path stay pinned, the fixed-volume check stays, and apply still requires a certified profile with catalog, output and exact applied-count pins;
- ran the fixture-free smoke checks, including the negative test that proves an ordinary build rejects all six mutation entrypoints, before the private EN/UK fixtures are required, so that gate is now covered on machines without private game data;
- added an onboarding regression test that accepts a newer build while rejecting wrong locale slots, zero revisions and a stamp that disagrees with its game version;
- documented the audited macOS client, including why the signed bundle cannot be edited and where the writable cache actually lives.

## 0.2.0-preview — 2026-08-15

- completed a coherent 1,820-ID Russian MVP across settings, login, UI, collection, quests, inventory, progression, errors, characters, skills, and combat;
- ran all bulk translation inside Codex with Terra and targeted terminology/mechanics review with Sol, without a third-party API integration;
- added full and conservative preview composition; the first supervised test applies only 570 records without `needs_review` and leaves the remaining 1,250 as English fallback;
- pinned the exact translation catalog plus compressed and raw LOC1 output hashes;
- added a separate compile-time `supervised_preview` build with an immutable embedded compatibility manifest; write commands reject external `--compat`;
- kept all installation writes disabled in the ordinary unsigned dev build, including restore and recovery;
- added all-process game/launcher/updater detection without process termination, local fixed-NTFS enforcement, and reparse-path refusal;
- added deterministic build reports, source-free overlay audit, supervised manifest generation, publish, and package scripts;
- preserved 41,282 LOC1 entries, locale ID 8, revision `F7BC8460`, content GUID/release, and byte-identical writer round-trips;
- prepared the controlled apply/restore procedure; real-client loader/font acceptance still requires the observed user session.

## 0.1.2-dev — 2026-08-15

- fixed packaged config discovery when the executable is started from another working directory;
- reject unknown CLI options and values accidentally supplied to boolean flags;
- require new, non-reparse output paths outside game/launcher/state roots for every workspace-writing command;
- publish this version as the only current dev handoff; 0.1.0/0.1.1 packages are superseded.

## 0.1.1-dev — 2026-08-15

- disabled all real-installation writes in the unsigned dev CLI independently of editable compatibility JSON;
- fixed journal transaction-id traversal and constrained recorded target/backup paths;
- pinned certified translation catalog hashes and minimum applied coverage;
- revalidated the full binary/i18n tuple under the execution lock immediately before commit;
- made restore/recovery require an exact compatibility tuple and fixed pre-backup crash recovery;
- added strict UTF-8/JSON parsing, canonical job/source/hint/id binding, partial-import refusal and stale-human invalidation;
- added release-only approved coverage, reviewer metadata, sensitive-text fallback and screenshot QA gates;
- protected numbers, compact units, URLs/email, tag nesting, newlines, Unicode normalization and invisible controls;
- replaced the external Batch/API plan with local Codex Terra→Sol chunks, deterministic checkpoints and a 500-item pilot;
- passed the full seven-test smoke suite after security hardening.

## 0.1.0-dev — 2026-08-15

- reverse-engineered conservative LOC1 schema-4 reader/writer;
- proved byte-identical EN/UK raw round-trip on `Prod_0.60.0_26`;
- added Brotli read/write and post-build verification;
- added source-free hash-ID RU overlay and private deduplicated EN+UK jobs;
- added stale-source, placeholder/tag, newline and length QA;
- added model-result importer that always creates drafts;
- added exact build compatibility matching and blocked current build gate;
- added immutable backup, same-volume atomic replacement, mutex/lockfile, transaction journal, hash-based recovery and exact restore;
- added seven smoke tests including a synthetic transactional install;
- moved project to .NET 10 LTS and produced a self-contained unsigned Windows dev build.
