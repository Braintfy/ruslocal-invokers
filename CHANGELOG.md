# Changelog

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
