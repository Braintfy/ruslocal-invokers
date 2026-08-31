# Windows patcher self-updates / Автообновление патчера

## Для пользователей

Начиная с **3.1.5**, программа сама проверяет обновления в GitHub при запуске и по кнопке «Проверить». Если есть новая версия, появится предложение скачать и установить её. Нажмите «Да»: после проверки загрузки патчер закроется, установщик обновит программу и откроет её снова. Можно отказаться и продолжить работу. Кнопка «Обновить патчер» запускает отдельную проверку.

Версии 3.1.2–3.1.4 не умеют обновлять свой EXE: один раз скачайте актуальный установщик по прежней ссылке, закройте окна патчера и установите поверх старого. Перевод игры, выбранная папка и резервные копии сохранятся. Обновление самого перевода остаётся отдельным действием. Подпись Authenticode пока не оформлена — предупреждение Windows о неизвестном издателе возможно.

## User behavior

From 3.1.5 onward, startup and Check also check the fixed application-update channel. Updates require explicit consent. The GUI downloads and validates Setup, exits normally, and Setup restarts the GUI after successful installation. Canceling the offer or a failed network check does not block normal translation checks. Old binaries need one manual upgrade first.

This is not a background service or scheduled task. No Git client is required. No game files are changed by application installation; applying a translation is a separate operation.

## Trust boundary

- Fixed pointer: `https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/patcher-update.v1.json`.
- Separate protocol/kind from translation data, using the reviewed embedded P-256 key. Signing this channel authorizes **code execution**, not just text updates. Protect and review the signing key accordingly; never commit or upload it.
- Strict signed payload: Windows x64 channel, canonical three-part version, increasing sequence, repository-specific installer URL, byte count, SHA-256 and expiration. The client verifies the signature before trusting any field. Maximum installer size: 512 MiB; maximum manifest lifetime: 180 days. The publisher defaults to 120 days.
- The downloaded file is verified again while held open against replacement. Its PE product name must match the signed version. Redirects are restricted to GitHub and the expected GitHub download hosts.
- A signed acceptance record at `%LOCALAPPDATA%\InvokersRu\self-update` rejects lower sequences, lower versions and changed payloads at the same sequence. The updater does not install a version equal to or older than the running app. Reparse-point cache paths are refused. Expired or invalid metadata never authorizes an install.
- Fixed Setup arguments only; no shell or manifest-provided command line. Setup waits for the patcher's running mutex; it never kills the game, launcher or another patcher window. Close other patcher windows before updating.
- Authenticode is independent and still pending. The data-manifest signature is **not** a Windows publisher certificate.

## Maintainer workflow: publish into the same release

Keep the user-facing release page and old alias URLs, but publish each new version under a **new immutable installer filename**. Do not overwrite an EXE referenced by a signed manifest, even to rebuild the same version. Bump the application version for changed bytes.

1. Update the GUI/CLI project versions and publish/build defaults. Build a new payload and installer using [Windows self-build](windows-self-build.en.md) and [release safety](windows-release-safety.md). For the official channel, preserve the original bootstrap catalog/profile pair needed to authenticate existing installations; the latest translation catalog is delivered separately. Never publish a catalog with a mismatched bootstrap hash.
2. Test the built payload and installer packaging. Write short Russian user-facing notes to a UTF-8 text file. Commit/push reviewed sources; keep the original release tag unchanged and include a source commit link in the release description.
3. Check the protected key directory's `patcher-history-v1/sequence-*.json` files. Choose the next consecutive number (this is **independent** of translation-data sequence 5). Never delete/reset history or reuse a reserved sequence. Keep it with your offline signing backups.
4. Run from the repository root (replace paths, version and sequence with real values):

```powershell
$env:INVOKERSRU_DOTNET = 'C:\path\to\dotnet.exe'
.\scripts\update-channel\New-PatcherUpdate.ps1 `
  -Installer 'work\installer-3.1.5-preview\InvokersRu-3.1-Preview-3.1.5-preview-win-x64.exe' `
  -Version '3.1.5' -ReleaseTag 'v3.1.4-preview' -Sequence 1 `
  -PrivateKey 'C:\protected-outside-repository\update-signing-private.pem' `
  -OutputDirectory 'work\patcher-channel-seq1' `
  -NotesFile 'work\patcher-notes.txt'
```

The output directory must not exist; its parent must exist. The command checks the EXE product version, signs and verifies against the production public key, reserves the sequence in protected history, then writes the signed envelope and readable payload. If it fails after reservation, recover the exact signed envelope from history; do not sign a different payload at that sequence.

5. In GitHub → Releases → the existing release → Edit, upload the canonical versioned EXE and checksum. Upload a versioned copy of the envelope and readable payload for audit. For this release the canonical filename is `InvokersRu-3.1-Preview-3.1.5-preview-win-x64.exe`, inside `v3.1.4-preview`.
6. Download the **public** canonical URL and verify its length/SHA-256 against the signed payload. Preserve old direct-link aliases by first archiving their previous assets, then publishing the new installer under the old alias names. Update each alias checksum and explain its internal version. Do not point the signed manifest at those mutable aliases.
7. **Publish the pointer last:** in release `invokersru-update-channel-v1`, archive the old `patcher-update.v1.json` if one exists and upload the new envelope with exactly that name. Verify the public bytes/signature and download through the production updater. Do not touch `update-envelope.v1.json`, translation assets or DMGs for an EXE-only update.
8. Installed 3.1.5+ patchers offer the next higher app version. A same-version envelope renewal with a higher sequence refreshes expiration but does not reinstall. Renew before expiry even if the app version is unchanged. Existing older app versions that never contained this updater still need manual installation.

There is deliberately no GitHub workflow holding the private key. A source commit or uploaded EXE alone does not publish an authorized update; the signed pointer is required. Forks must define their own reviewed channel/key and build their own patcher, not request the upstream private key.

## Verification and limitations

Fixture-free smoke tests cover manifest signatures, strict fields, expiry, rollback/equivocation, URL restrictions, HTTP redirects, bounded/truncated downloads, hashes and fixed handoff arguments. Packaging tests check the fixed conditional GUI restart and mutex guard. UI rendering checks cover normal/narrow windows and enlarged text. A Windows 10 VM install and full interactive installer handoff need separate manual verification; do not describe source/packaging checks as an end-to-end installation test.

Installer references: [command-line switches](https://jrsoftware.org/ishelp/topic_setupcmdline.htm), [Run section](https://jrsoftware.org/ishelp/topic_runsection.htm), [mutex checks](https://jrsoftware.org/ishelp/topic_isxfunc_checkformutexes.htm).
