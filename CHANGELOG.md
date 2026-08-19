# Changelog

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
