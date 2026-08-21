# Building the Windows patcher from source

This guide is for players, auditors, and game developers who prefer not to run a prebuilt EXE. It explains how to build InvokersRu from reviewed source. An ordinary build, player publish, and installer build **do not read or modify the game**. A normal `dotnet build` writes `bin\`/`obj\` inside the clone, publish/installer outputs stay below `work\`, and .NET may populate the user's SDK/NuGet cache. Game data changes only later, when a user launches the resulting patcher, closes the game, and explicitly confirms localization installation.

The guide covers:

- an ordinary diagnostic build with game writes disabled;
- a self-contained Windows player with the supervised write capability;
- an optional Inno Setup installer;
- hash and Authenticode verification;
- the official signed update channel versus an independently controlled key;
- the localization format and a handoff path for the game developers.

## 1. What you are auditing

The Windows player contains two adjacent applications:

- `InvokersRu.Gui.exe` — the graphical shell;
- `InvokersRu.Cli.exe` — file inspection, LOC1 composition, transactional apply, restore, and recovery.

A normal `dotnet build` compiles Core without the production capability to mutate the game. That capability is selected only by the special Release build in `scripts\publish-windows-preview.ps1`. The script supplies guarded MSBuild properties, embeds the bootstrap exact profile and compatible-revision trust boundary, and probes the published CLI before it creates the payload manifest.

Self-contained means the player does not require a separately installed .NET Runtime. It is a normal multi-file directory, not a packed single-file executable. Copying only the two EXE files and discarding their neighboring DLL/JSON files creates an incomplete application.

Two independent signatures are relevant:

1. **ECDSA P-256 update-data signatures** authenticate the remote translation catalog and compatibility profiles. The public key is embedded in the CLI. This mechanism is free and is not an EXE signature.
2. **Authenticode** identifies the publisher of Windows executables and the installer to Windows and SmartScreen. It is optional for a local audit build; an unsigned public build can still trigger a Windows reputation warning.

## 2. Prerequisites

Required:

- x64 Windows 10 build 14393 (version 1607, including Enterprise 2016 LTSC) or
  newer, or Windows 11 x64;
- Git;
- the .NET SDK selected by the root `global.json` — currently `10.0.302`;
- Windows PowerShell 5.1 or PowerShell 7 (`pwsh`).

Creating a Setup executable additionally requires **Inno Setup 6**. The build script searches the normal locations for `ISCC.exe`; use `-IsccPath` for a nonstandard installation.

Authenticode additionally requires the Windows SDK `signtool.exe`, a code-signing certificate, and an RFC 3161 timestamp URL. A random self-signed certificate is not proof of a public publisher and is useful only for a local experiment.

Check the environment from PowerShell:

```powershell
git --version
dotnet --version
powershell.exe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
```

Windows PowerShell 5.1 alone is sufficient when `pwsh` is not installed. If `dotnet --version` reports that the SDK selected by `global.json` is unavailable, install that SDK from the official .NET site. Do not edit `global.json` merely to bypass the check; doing so changes the reproducible build environment.

## 3. Make a clean clone and record its identity

```powershell
git clone https://github.com/Braintfy/ruslocal-invokers.git
Set-Location .\ruslocal-invokers
git status --short
git rev-parse HEAD
```

`git status --short` should be empty immediately after cloning. Save the value printed by `git rev-parse HEAD`; it is the exact source commit behind your binaries. To audit a particular release, start with a new clone and switch to its published tag or commit instead of mixing files from different revisions.

You can also confirm that Git is not tracking game data or local secrets:

```powershell
git ls-files work
git ls-files '*.bin' '*.bin.br' '*.exe' '*.dll'
git status --short
```

A clean repository does not list extracted EN/UK tables, compiled binaries, or anything below `work\`. `.gitignore` explicitly excludes `*.bin`, `*.bin.br`, `manifest.dat`, private fixtures, local state, and executable build products.

## 4. Ordinary diagnostic build

First build the solution with game writes disabled:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --fixture-free-only
```

Equivalent PowerShell 7 invocation:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --fixture-free-only
```

`--fixture-free-only` tests the parser, validator, profiles, GUI contract, and mutation gate without proprietary EN/UK files. Run the specialized fixture-free signed-update protocol and exact-profile adapter suites separately:

```powershell
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-update-only
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-profile-adapter-only
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-update-coordinator-only
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-update-lkg-hardening-only
```

Full corpus tests need private files from the auditor's own installation and therefore cannot be reproduced by a public clone alone.

Test the signed-data release utility separately. It creates a one-time key only in a temporary directory, checks the signature with the production verifier, checks tamper rejection and sequence non-reuse, and then removes the temporary data:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1
pwsh -NoProfile -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1
```

None of the commands in this section access the game localization cache or install a translation.

## 5. Publish the self-contained graphical player

Choose a unique local version and a new output directory. The script intentionally refuses to overwrite an existing result.

```powershell
$version = '3.1.1-local.1'
$payload = ".\work\publish\windows-$version"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\publish-windows-preview.ps1 `
  -OutputDirectory $payload `
  -AppVersion $version `
  -TranslationCatalog '.\translations\ru_RU.jsonl' `
  -RuntimeCacheProfile '.\config\runtime-cache-profile.0.60.1247.json' `
  -SignedUpdateChannelConfig '.\config\signed-update-channel.v1.json'
```

PowerShell 7 equivalent:

```powershell
pwsh -NoProfile -File .\scripts\publish-windows-preview.ps1 `
  -OutputDirectory $payload `
  -AppVersion $version `
  -TranslationCatalog '.\translations\ru_RU.jsonl' `
  -RuntimeCacheProfile '.\config\runtime-cache-profile.0.60.1247.json' `
  -SignedUpdateChannelConfig '.\config\signed-update-channel.v1.json'
```

The profile above is the embedded fallback for the currently certified game build. Use a different profile only when it has actually been certified for the selected catalog. Editing a hash in JSON by hand is not certification: the publish script cross-checks the catalog, profile, embedded resources, and the reported properties of the CLI.

The script restores runtime packs from the official NuGet endpoint, publishes a normal multi-file .NET runtime, retains exactly the two allowed EXE files, rejects scripts in the player payload, and creates:

- `BUILD-RECEIPT.json` — SDK version, exact GUI/CLI/catalog/profile hashes, and Authenticode state;
- `PAYLOAD-SHA256.json` — the complete allowlist of paths, lengths, and SHA-256 values;
- `translations\ru_RU.jsonl` and `profiles\...json` — public data, never original game files.

Inspect the published directory:

```powershell
& "$payload\InvokersRu.Cli.exe" trusted-runtime-cache-info
& "$payload\InvokersRu.Cli.exe" update-status --json
Get-Content -Raw -LiteralPath "$payload\BUILD-RECEIPT.json"
Get-Content -Raw -LiteralPath "$payload\PAYLOAD-SHA256.json"
Get-FileHash -Algorithm SHA256 -LiteralPath "$payload\InvokersRu.Gui.exe"
Get-FileHash -Algorithm SHA256 -LiteralPath "$payload\InvokersRu.Cli.exe"
```

`update-status` reads only the already verified local update cache. A network refresh is a separate `update-refresh` command or GUI action. The patcher never downloads or executes a replacement EXE.

Run the packaging tests under both shells. They validate the manifest, allowlist, fixed installation directory guard, and negative cases without launching an installer or the game:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\test-windows-packaging.ps1 `
  -PayloadDirectory $payload -AppVersion $version

pwsh -NoProfile -File .\scripts\test-windows-packaging.ps1 `
  -PayloadDirectory $payload -AppVersion $version
```

## 6. Optional installer

Without Inno Setup, run `InvokersRu.Gui.exe` directly from the complete `$payload` directory. To create a normal Setup executable, install Inno Setup 6 and run:

```powershell
$installerOutput = ".\work\installer-output-$version"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-installer.ps1 `
  -InputDirectory $payload `
  -AppVersion $version `
  -OutputDirectory $installerOutput `
  -InstallerBaseName "InvokersRu-$version-win-x64"
```

Add `-IsccPath` with the full path when `ISCC.exe` is installed outside its normal locations. The script re-verifies every input against `PAYLOAD-SHA256.json`, copies only the allowlisted payload to a fresh staging directory, invokes Inno Setup, re-verifies the stage, and writes a `.sha256` sidecar.

The installer is **not run automatically**. It only installs the payload below `%LOCALAPPDATA%\Programs\InvokersRu`, creates a Start Menu shortcut, and registers a standard per-user uninstaller. It has no service, scheduled task, patcher auto-run, administrator request, or process-killing action.

An unsigned installer is an expected result for a local source audit. Windows can show `Unknown publisher`, SmartScreen, or reputation warnings. A compiler flag cannot remove those warnings and cannot manufacture a publisher signature.

## 7. Rebuilding after a change

The publish scripts intentionally refuse to overwrite an existing player or installer. For another build, choose a new `AppVersion`, `OutputDirectory`, and `InstallerBaseName`. This preserves the previous artifact for comparison and prevents old and new DLLs from being mixed.

Identify the kind of change before rebuilding:

- **GUI/CLI code only:** republish the player and installer with a new version, then repeat smoke, packaging, and hash checks.
- **Russian catalog only:** the old embedded exact profile no longer matches because it pins catalog SHA-256, final LOC1 SHA-256, and composition counts. Create a new signed data release or recertify the embedded profile; simply replacing `ru_RU.jsonl` should make the build fail.
- **New game version:** try the built-in `compatible-revision` path first. For a supported raw LOC1 schema-4 family, a fresh signed catalog is normally enough: exact EN+UK matches are applied and all other rows remain English. Publish an exact profile when complete reproducible coverage for that tuple is required; rebuild the EXE only when code or the format/trust boundary changes.
- **New update key, endpoint, URL policy, or LOC1 writer:** a new EXE is required because the trust boundary or code has changed.

Do not clean an old output in place or copy build files manually. Compare `BUILD-RECEIPT.json`, `PAYLOAD-SHA256.json`, SHA-256 values, and the source commit between two separate output directories.

## 8. Hash and Authenticode checks

Verify the installer and its sidecar:

```powershell
$installer = Get-Item -LiteralPath `
  (Join-Path $installerOutput "InvokersRu-$version-win-x64.exe")

(Get-FileHash -Algorithm SHA256 -LiteralPath $installer.FullName).Hash
Get-Content -LiteralPath ($installer.FullName + '.sha256')
Get-AuthenticodeSignature -LiteralPath $installer.FullName | `
  Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
```

`NotSigned` is expected for an unsigned local build. SHA-256 still proves that the artifact has not changed since your build. Finish the source audit with:

```powershell
git status --short
git diff --exit-code
git rev-parse HEAD
```

For a real Authenticode build, pass `-SignToolPath`, `-CertificateThumbprint`, and `-TimestampUrl` to the publish script together. Supplying only a subset is rejected. Configure an Inno Setup signing tool separately, then build the installer with `-InnoSignToolName` and the same `-ExpectedSignerThumbprint`. `build-installer.ps1` verifies the signed payload and final Setup. In `-VerifyOnly` mode it deliberately rejects `-InnoSignToolName`, because no installer is being created in that mode.

See [windows-release-safety.md](windows-release-safety.md) for the complete release trust model.

## 9. Official update channel versus an independent key

`config\signed-update-channel.v1.json` contains only a public trust anchor:

- a fixed HTTPS URL for the signed envelope;
- a `key_id`;
- an ECDSA P-256 public key as DER SPKI/Base64.

There is no private key in the repository, payload, or installer. A source build using the official config accepts the same signed catalogs and exact compatibility profiles as the official patcher. It does not need the official private key.

A remote manifest cannot replace the embedded endpoint or public key. The patcher verifies the signature, lifetime, monotonic `sequence`, revocations, compressed and uncompressed size/SHA-256, minimum patcher version, and either the selected exact profile or the authenticated content-family authority used to materialize a locally pinned compatible-revision profile. Verified data is stored in a content-addressed cache below `%LOCALAPPDATA%\InvokersRu\updates`. On a network failure, only a previously verified last-known-good bundle or the embedded bootstrap catalog/profile may be used under the expiry and installed-state rules.

For an independent experimental channel, test the release utility first, then generate a key in a **new external directory outside the repository and any synchronized/shared folder**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\New-UpdateSigningKey.ps1 `
  -OutputDirectory $externalNewSigningDirectory
```

The parent of `$externalNewSigningDirectory` must already exist, while the final directory must not. The tool creates `update-signing-private.pem`, `update-signing-public.json`, and `update-signing-state.json`, restricts their ACL to the current user, and never prints the private key.

Copy only `key_id` and `subject_public_key_info_b64` from `update-signing-public.json` into a separate `invokers-ru-update-channel` config. The receiving field is named `public_key_spki_base64`:

```json
{
  "schema": 1,
  "kind": "invokers-ru-update-channel",
  "envelope_url": "https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/update-envelope.v1.json",
  "key_id": "PUBLIC_KEY_ID_FROM_KEYGEN",
  "public_key_spki_base64": "PUBLIC_SPKI_BASE64_FROM_KEYGEN"
}
```

This only shows the file shape; the placeholder values are not usable keys. The standard publish process verifies and pins the fixed endpoint shown above; an arbitrary `latest/download` URL is rejected. Save the config as strict UTF-8 without a BOM inside the repository or below ignored `work\`, and pass it as `-SignedUpdateChannelConfig` when publishing. Never copy the PEM or signing state into the repository.

The current URL policy intentionally permits update assets only from `Braintfy/ruslocal-invokers` GitHub Releases. A config edit alone cannot trust an arbitrary server or fork. A developer operating another repository must update and review `SignedUpdateUrlPolicy`, its redirect allowlist, and release-tool URL construction, then ship a new patcher containing the new public key. An already-built patcher cannot remotely replace its root key.

See [update-channel.md](update-channel.md) for signed data-release creation. Translation/profile data releases do not require a new EXE while the LOC1 format, fixed paths, protocol, URL policy, and trust key remain unchanged.

## 10. How the localization works

On Windows, the game stores downloaded localization under:

```text
%USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n
```

The patcher uses one fixed tuple:

```text
dl_en_US.bin      authoritative English source
dl_uk_UA.bin      Ukrainian slot and the only mutated game file
dl_uk_UA.bin.ver  game-version stamp
```

The player must therefore select **Ukrainian**, wait for its download, and fully close the game and launcher before inspection. Otherwise `dl_uk_UA.bin` may not exist or may be changing concurrently.

The supported container is strict `LOC1` schema 4. For the current profile family, EN uses locale ID `1` and UK uses locale ID `8`. Both tables must have the same content GUID, schema, and complete ordered set of 64-bit keys. Each overlay ID is the key rendered as 16 uppercase hexadecimal digits.

Public `translations\ru_RU.jsonl` is a source-free JSON Lines overlay. Conceptually, each record contains:

```json
{"id":"16_HEX","source_sha256":"64_HEX","hint_sha256":"64_HEX_OR_NULL","translation":"Russian text","status":"draft"}
```

- `id` binds the translation to a key, not a line number;
- `source_sha256` is SHA-256 of the exact UTF-8 English string; an EN change makes the record stale and ineligible;
- `hint_sha256` is SHA-256 of the exact Ukrainian context hint; private export/import and validation use it to detect context changes;
- `translation` is Russian plaintext; the overlay contains no English or Ukrainian plaintext;
- `status` (`draft`, `reviewed`, or `approved`) and the other fields carry provenance, risk, and review state.

`hint_sha256` does not replace `source_sha256`: compatible-revision composition requires both exact matches, while an exact profile additionally pins the entire UK base and final raw LOC1. Release preparation should validate with the Ukrainian hint package so a context change cannot pass unnoticed.

Composition walks the ordered UK base entries:

1. If the EN value is null/missing, the UK/service value remains (`base_fallback`).
2. If ID, current `source_sha256`, allowed status/policy, and mechanical validation all pass, Russian is written (`applied_ru`).
3. A missing, stale, policy-blocked, or invalid translation becomes exact English (`english_fallback`), with separate reason counters.
4. The writer preserves the UK header, locale identity, and key order; it rebuilds values and the required data-section length, then parses the result again.
5. The exact profile pins EN, UK, and stamp SHA-256, catalog SHA-256, expected raw output SHA-256, and every composition count. Any mismatch blocks the write.

In `compatible-revision` mode, the complete observed tuple, ordered keyset, selected catalog, locally materialized output, and counts are also pinned before the transaction, but rows are admitted independently. A new or changed row therefore becomes English fallback instead of receiving stale Russian text.

The only persistent target inside the game cache is `dl_uk_UA.bin`; `dl_en_US.bin` and the stamp are read but not modified. A transaction can briefly create strictly named temp/displaced/rollback files beside the target. On a race or failure they are retained for controlled recovery instead of being hidden. The patcher does not inject into a process, install a hook/driver/service, or change game executables, DLLs, memory, accounts, the network protocol, or the signed application package. Immutable backups, state, and transaction journals are kept separately below `%LOCALAPPDATA%\InvokersRussian\runtime-cache`.

## 11. A new game version: compatible mode and an optional exact profile

After a game update, select Ukrainian, wait for the download, and close the game. An ordinary CLI build can safely capture a read-only profile:

```powershell
dotnet build .\src\InvokersRu.Cli\InvokersRu.Cli.csproj -c Release
New-Item -ItemType Directory -Path '.\work\profiles' -Force | Out-Null

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\new-runtime-cache-profile.ps1 `
  -OutputProfile '.\work\profiles\runtime-cache-profile.new.json'
```

The generator records the stamp, GUID, content versions, locale/release revisions, entry count, and EN/UK/stamp SHA-256, but intentionally emits `readiness=blocked` and `certified=false`. It is an observation, not write authorization.

Certification needs private files from the operator's **own official installation**, retained only below `work\`:

- exact EN and UK containers plus the stamp;
- schema `4`, content GUID/versions, locale IDs/revisions, and release revisions;
- the complete ordered key set and its SHA-256: hash the ASCII bytes `ID_HEX16 + "\n"` for every target ID in LOC1 order, including LF after the final ID;
- source-container and catalog SHA-256;
- a validation report with no blocking errors;
- `applied_ru`, `english_fallback`, `base_fallback`, missing/stale/rejected/policy/needs-review counts;
- final raw LOC1 SHA-256.

A read-only CLI command can build a review output into a new non-game path:

```powershell
dotnet run --project .\src\InvokersRu.Cli\InvokersRu.Cli.csproj -c Release -- build `
  --english $privateEnglish `
  --base $privateUkrainian `
  --translations .\translations\ru_RU.jsonl `
  --output .\work\profile-review\dl_uk_UA.ru.bin `
  --report .\work\profile-review\build-report.json `
  --include-draft --raw
```

After reviewing the output, create the exact compatibility input without manually copying hashes or counters:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\New-SignedUpdateCompatibilityProfile.ps1 `
  -RuntimeProfile '.\work\profiles\runtime-cache-profile.new.json' `
  -EnglishLoc1 $privateEnglish `
  -BaseLoc1 $privateUkrainian `
  -Stamp $privateStamp `
  -Catalog '.\translations\ru_RU.jsonl' `
  -BuiltLoc1 '.\work\profile-review\dl_uk_UA.ru.bin' `
  -BuildReport '.\work\profile-review\build-report.json' `
  -Output '.\work\profile-review\signed-compatibility.json'
```

The generator rechecks the tuple, catalog, report, final LOC1, composition counts, and ordered keyset, then exercises the production verifier/adapter. It consumes snapshots only, writes one new output, and does not discover the installed game. The exact flags must match the selected preview policy; a real release uses only human-reviewed data and its release gate. Feed the generated JSON into the signed data release described in [update-channel.md](update-channel.md). Never mark a profile certified until the catalog hash, output hash, and counts have been reproduced.

A new signed data release lets an already installed compatible patcher support that game version without downloading a new EXE. A new EXE is required when the game changes the paths, LOC1 schema/layout, locale model, update protocol, trust key, or when patcher code itself needs a fix.

## 12. Handoff to the game developers

The repository is available under the MIT License, but it does not grant rights to game trademarks or assets. Game developers have two practical integration paths.

### Import the overlay into the official pipeline

1. Take the authoritative EN table from the internal build pipeline.
2. Match its 64-bit key to `id` in `translations\ru_RU.jsonl`.
3. Compute SHA-256 of the exact UTF-8 EN bytes and accept a record only when it matches `source_sha256`.
4. Select acceptable review statuses and human-review `needs_review`, sensitive, and context-required records.
5. Keep English fallback for missing/stale records and return their ID list to the community.
6. Emit the official RU asset in the native build pipeline and run internal UI, screenshot, and functional QA.

If the internal localization system does not use the same 64-bit keys, create a one-time mapping from the authoritative table. The repository has no plaintext EN, so the mapping cannot—and should not—be reconstructed from a hash alone.

### Add an official `ru_RU` locale

The preferred product solution is a dedicated Russian locale, locale ID, and `dl_ru_RU.bin`, plus `ru_RU` in the server manifest/language selector. This avoids reusing Ukrainian and removes the need for a community patcher. Russian values and review metadata can be imported from the overlay, while headers, revisions, signing, and CDN distribution remain the responsibility of the official pipeline.

Before import, developers should obtain:

- the commit SHA and SHA-256 of `translations\ru_RU.jsonl`;
- the glossary/style guide and chosen status policy;
- the EN content GUID/version and ordered-keyset hash used for export;
- composition and validation reports;
- fallback/stale/missing/context-required ID lists;
- license confirmation and attribution from `LICENSE`/`NOTICE.md`.

Vendor EN/UK tables, user caches, a generated `dl_uk_UA.bin`, private translation jobs, signing PEM/state, and personal filesystem paths must never be committed. Always inspect `git status --short` and the exact staged-file list before publishing.

## 13. Auditor checklist

- The clone is clean and its commit SHA is recorded.
- The SDK matches `global.json`.
- The ordinary build and fixture-free smoke tests pass.
- The signed-update self-test passes under Windows PowerShell 5.1 and PowerShell 7 when both are available.
- The player is produced only by `publish-windows-preview.ps1` into a new `work\...` path.
- The expected catalog/profile/public update config are embedded; no private key is present.
- `BUILD-RECEIPT.json` and `PAYLOAD-SHA256.json` are reviewed.
- `test-windows-packaging.ps1` passes.
- The installer hash matches its `.sha256`; Authenticode is reported honestly as `Valid` or `NotSigned`.
- The build did not launch the game or modify its files.
- Before actual application, Ukrainian is selected and downloaded, and the game and launcher are closed.
