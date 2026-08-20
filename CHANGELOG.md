# Changelog

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
