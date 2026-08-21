# Community localization kit

This folder is an English-only, local workflow for creating an Invokers: Titan Legacy localization in another language. Translation may be written manually or in a Codex conversation. The workflow does **not** call a translation API and does not require an API key. The translation pipeline itself works offline after tools are installed; the platform package builders may contact NuGet when the required .NET runtime packs are not already cached.

The kit reuses the repository's audited LOC1 parser, deterministic job format, source-bound overlay, token validator, English fallback, and exact compatibility checks. It does not include original English/Ukrainian game tables, private jobs, secrets, compiled binaries, or a generated LOC1 file.

## Scope and hard limits

- A target text language is not automatically a supported game locale. The current audited installer path uses the **existing `uk_UA` slot only** (`dl_uk_UA.bin`, locale id 8). Any language can be authored for that slot if the game's fonts and layout can render it.
- Before capture, build, or installation, select Ukrainian in the game and wait for the client to download that slot. Before installation or restore, fully quit the game and launcher.
- A different slot, filename, locale id, LOC1 schema, cache path, or storage model requires a code-level implementation and a new compatibility review. Editing `language-config.json` cannot make it safe; the pipeline rejects it.
- Every installable profile is exact to one EN/base/stamp tuple, catalog SHA-256, composition, and raw LOC1 output. An unknown game update fails closed.
- Missing, changed, stale, rejected, or untranslated records fall back to authoritative English. The pipeline never guesses how to patch an unknown source.
- Client-side files cannot localize server-rendered store/event text and cannot add glyphs missing from the game's fonts.

## What is public and what stays private

Safe public project material:

- a source-free translation overlay such as `xx_XX.jsonl`;
- this kit, prompts, glossary, style guide, build receipts, and compatibility hashes;
- source code and installer-builder scripts.

Keep private and ignored:

- `dl_en_US.bin`, `dl_uk_UA.bin`, version stamps copied from an installation, and any other original game asset;
- `private.jobs.jsonl`, because it contains English source and optional Ukrainian context text;
- model/manual result batches while they still contain private source context;
- generated raw LOC1 files and any installer that embeds one;
- signing keys, local state, backups, and personal filesystem paths.

Use a directory below repository `work/`; it is already ignored. The platform package builders deliberately include only the source-free catalog and compose the LOC1 from each player's own installed files.

## Prerequisites

- This repository and the .NET 10 SDK pinned by `global.json`.
- Python 3.10 or newer for `scripts/pipeline.py`.
- PowerShell 5.1 or newer for the Windows package builder.
- On macOS: Apple Silicon, Xcode command-line tools, and `hdiutil` for the local DMG builder.
- Optional on Windows: Inno Setup 6.3 or newer (`ISCC.exe`) for a Setup EXE. Without it, the builder still creates a complete ZIP.

Build the read-only CLI first:

```powershell
dotnet build .\src\InvokersRu.Cli\InvokersRu.Cli.csproj -c Release
python .\community-localization-kit\scripts\pipeline.py self-test
```

On macOS/Linux, replace backslashes with slashes and use `python3`.

## 1. Define the language

Copy and edit these templates:

- [`templates/language-config.example.json`](templates/language-config.example.json)
- [`templates/glossary.example.json`](templates/glossary.example.json)
- [`templates/style-guide.example.md`](templates/style-guide.example.md)

Keep `injection_slot` unchanged. Set a safe lowercase `pack_id`, the target language name, and a conservative BCP 47 tag. Choose a policy:

- `preview-drafts` includes validated draft rows and is appropriate for personal/community testing;
- `release-approved` accepts only fully human-approved records and enforces the repository's release metadata and coverage policy.

Machine output must remain a draft. Do not label a model-only catalog as reviewed or approved.

## 2. Capture a private exact tuple and export jobs

In the game, select Ukrainian, wait for the download, then fully close the game. Copy or reference these three files from your own runtime cache:

- `dl_en_US.bin` — authoritative source;
- `dl_uk_UA.bin` — existing slot whose locale header and key order will be preserved;
- `dl_uk_UA.bin.ver` — exact game-version stamp.

Windows default cache:

```text
%USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n
```

macOS iOS-app cache:

```text
~/Library/Containers/<installation UUID>/Data/Documents/i18n
```

Prepare a new ignored workspace:

```powershell
python .\community-localization-kit\scripts\pipeline.py prepare `
  --config .\work\my-language\language-config.json `
  --english .\work\private-fixtures\dl_en_US.bin `
  --base .\work\private-fixtures\dl_uk_UA.bin `
  --stamp .\work\private-fixtures\dl_uk_UA.bin.ver `
  --workspace .\work\my-language\jobs-v1
```

`prepare` parses and byte-roundtrips both LOC1 containers, verifies schema/GUID/key-count/locale compatibility, creates a blocked source profile, exports deterministic jobs, and writes a path-free hash receipt. It never writes to the installed game.

When updating an existing overlay, pass `--existing-catalog PATH`; unchanged source-bound rows will not be exported again.

Optionally split the private JSONL into deterministic reviewable chunks using the existing local wrapper:

```powershell
pwsh -NoProfile -File .\scripts\split-work-items.ps1 `
  -InputJobs .\work\my-language\jobs-v1\private.jobs.jsonl `
  -OutputDirectory .\work\my-language\chunks-v1 `
  -PromptPath .\community-localization-kit\prompts\translation.md `
  -GlossaryPath .\work\my-language\glossary.json
```

Do not paste the complete private corpus into an issue, commit, or public model service.

## 3. Translate locally or through Codex

Use [`prompts/translation.md`](prompts/translation.md) for first-pass translation and [`prompts/review.md`](prompts/review.md) for a separate review pass. Replace their variables with your reviewed language config, style guide, glossary, and one private job chunk.

Codex can translate batches directly in the conversation or workspace; no third-party API is required. Save the response as strict UTF-8 JSONL. Each line must match [`templates/model-result.schema.json`](templates/model-result.schema.json):

```json
{"job_id":"ru-0123456789abcdef01234567","translation":"Target text","model":"manual-or-model-name","prompt_version":"community-translation-v1","confidence":"high","needs_review":false,"issue_codes":[]}
```

Never add a catalog status or approval claim to a result row. `import-results` creates draft overlay records and binds them to the exact current source.

## 4. Import and verify results

```powershell
python .\community-localization-kit\scripts\pipeline.py import-results `
  --config .\work\my-language\language-config.json `
  --english .\work\private-fixtures\dl_en_US.bin `
  --base .\work\private-fixtures\dl_uk_UA.bin `
  --jobs .\work\my-language\jobs-v1\private.jobs.jsonl `
  --results .\work\my-language\results-v1.jsonl `
  --output-catalog .\work\my-language\catalog-v1.jsonl
```

For a deliberate partial batch, add `--allow-partial`. The importer and validator reject, among other things:

- a changed job id, source SHA-256, hint SHA-256, corpus GUID/version, or source-id binding;
- missing/extra placeholders, URLs, emails, rich-text tags, escaped newlines, literal numbers, percentages, and compact mechanic units;
- invalid tag nesting or a changed literal newline count;
- duplicate/unknown rows, unsupported fields, empty non-empty-source translations, invalid UTF-8, non-NFC text, NUL, unsafe control, zero-width, and bidi-control characters.

Warnings such as unchanged English or extreme length still need human review. Automated validation proves structural preservation, not translation quality or UI fit.

## 5. Review and approve

Review terminology and mechanics with a second pass, then test screenshots in every important menu, skill tooltip, quest, store card, and battle overlay. Context-deduplicated records need screenshot QA. Legal/privacy/account/payment text requires the repository's stricter human policy and should normally stay English until explicitly approved.

For a `release-approved` catalog, promote records only after real review and populate the existing catalog fields (`reviewer_ids`, `reviewed_at`, `review_revision`, `screenshot_qa`, and `legal_approved` where required). The CLI will reject invented or incomplete release metadata.

## 6. Build an exact LOC1 and local profile

Build into another new directory:

```powershell
python .\community-localization-kit\scripts\pipeline.py build `
  --config .\work\my-language\language-config.json `
  --source-profile .\work\my-language\jobs-v1\source-profile.json `
  --english .\work\private-fixtures\dl_en_US.bin `
  --base .\work\private-fixtures\dl_uk_UA.bin `
  --stamp .\work\private-fixtures\dl_uk_UA.bin.ver `
  --catalog .\work\my-language\catalog-v1.jsonl `
  --output-directory .\work\my-language\exact-build-v1
```

The command validates again, builds a raw LOC1 while preserving the base locale identity and key order, reparses it, and pins all source/catalog/output hashes and composition counts in `certified-runtime-profile.json`. It also creates `build-receipt.json`, which content-binds the exact language config, target-language identity, policy, catalog, profile, output hash, and composition counts. Keep these files together. "Certified" here means internally exact for the creator's local build; it is **not** an InvokersRu signature, developer approval, Authenticode signature, or Apple notarization.

The generated `*.loc1.bin` contains game-derived fallback text. Keep it private. Platform packages below do not copy it.

## 7. Create a Windows package or installer

From Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\community-localization-kit\scripts\Build-CommunityWindowsPackage.ps1 `
  -LanguageConfig .\work\my-language\language-config.json `
  -Catalog .\work\my-language\catalog-v1.jsonl `
  -CertifiedProfile .\work\my-language\exact-build-v1\certified-runtime-profile.json `
  -BuildReceipt .\work\my-language\exact-build-v1\build-receipt.json `
  -OutputDirectory .\work\my-language\windows-package-v1 `
  -Version 1.0.0
```

This creates a self-contained CLI package and ZIP. It embeds the exact local profile, includes only the source-free catalog, and uses the same guarded backup/journal/recovery implementation as the Windows patcher. The builder verifies that the config, language identity, policy, catalog, profile, output pins, and `build-receipt.json` all belong to the same build; mixed language-pack inputs are rejected. It has no official signed-update channel, so an exact profile must be rebuilt for a later game version.

To additionally create an unsigned Setup EXE, pass the full path to Inno Setup:

```powershell
  -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

Setup only installs the locally built patcher under the user's LocalAppData and creates a shortcut. It does not launch it or modify the game. The player later runs `CommunityLocalization.cmd`, checks the plan, and explicitly confirms apply/restore. An unsigned self-build can trigger `Unknown publisher` or SmartScreen; a compiler flag cannot create publisher trust. Authenticode is a separate owner-controlled process.

## 8. Create a macOS package and DMG

Run on macOS Apple Silicon:

```sh
bash community-localization-kit/scripts/build-community-macos-package.sh \
  work/my-language/language-config.json \
  work/my-language/catalog-v1.jsonl \
  work/my-language/exact-build-v1/certified-runtime-profile.json \
  work/my-language/exact-build-v1/build-receipt.json \
  work/my-language/macos-package-v1 \
  1.0.0
```

The builder emits a `.tar.gz` and local unsigned `.dmg`. The package includes an arm64 CLI, catalog, exact pins, and `CommunityLocalization.command`; it does not include EN/base/game-derived LOC1 data. At apply time it verifies the installed EN/base/stamp tuple, composes the exact output locally, verifies its pinned SHA-256, creates an immutable hash-named backup, rechecks that the game stopped and the target did not change, then performs one same-filesystem atomic replacement.

The macOS community package is intentionally a technical self-build, not the official Russian app. Terminal may need Full Disk Access to read the iOS app container. The DMG is not Developer ID signed or notarized.

If macOS or the terminal is killed during an operation, the conservative `operation.lock` directory can remain. First verify that no copy of the community patcher is running. Then remove only the exact lock directory printed by the tool under `~/Library/Application Support/InvokersCommunityLocalization/<pack_id>/operation.lock` and run Check again; never remove the backup directory.

## Installation rule for every target language

1. Select **Ukrainian** in Invokers.
2. Wait until its text finishes downloading.
3. Fully quit the game and launcher.
4. Run Check/Status first.
5. Apply only when the tool reports the exact compatible original tuple.
6. Leave the in-game selected language on Ukrainian. Opening the language selector can make the client download the official slot again and remove the community localization.

## Updating after a game release

Never edit a hash to make an old profile accept a new client. Capture the new EN/base/stamp tuple, run `prepare` with the previous catalog, translate only new or stale jobs, import, review, rebuild, and create new packages. Unchanged records remain valid because each overlay row is bound to the SHA-256 of its English source. Unknown/new rows remain English until translated.

## Trust and distribution

- The official InvokersRu 3.1 Windows patcher accepts data from its project-controlled ECDSA P-256 signed channel. A package made by this kit has no official channel and no project signature.
- If you operate a public fork, publish source, exact commit, build receipt, SHA-256 manifest, review policy, and honest signing status. Never reuse InvokersRu's name or keys as proof of endorsement.
- Do not publish a generated LOC1 or installer that embeds game-derived text without permission from the rights holder. Prefer source-free overlays that build from each player's own files.
- Keep signing keys outside the repository and make encrypted backups. Do not put keys, tokens, personal paths, private jobs, or game files in commits or release assets.
- A target language pack remains unofficial unless HitZone explicitly adopts it.

## Handoff to the game developers

If the developers want to adopt a language, provide:

1. the source-free overlay and its SHA-256;
2. target-language config, glossary, style guide, prompt versions, review policy, and provenance;
3. validation/build receipts, coverage/fallback counts, screenshot QA evidence, and known font/layout limitations;
4. the exact source commit and reproducible commands;
5. no extracted EN/base LOC1 unless the developer requests it through an authorized internal channel.

Developers can match each overlay `id` to their authoritative localization key, verify `source_sha256` against their English source, import only fresh approved rows, and emit a native locale asset through their own build/signing pipeline. They should assign a real locale slot, fonts, plural rules, QA, and server-side translations rather than shipping the community slot-replacement mechanism unchanged.
