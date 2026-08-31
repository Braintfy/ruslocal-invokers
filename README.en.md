# Invokers: Titan Legacy Russian localization

An unofficial Russian interface translation. This project is not affiliated with HitZone Inc.

[Русский](README.md) · [Download the latest release](https://github.com/Braintfy/ruslocal-invokers/releases/latest)

## Support

| Platform | Status |
| --- | --- |
| Windows PC | Supported |
| macOS, native client | Supported |
| Android | Not supported yet; planned for later |
| iOS / iPadOS | Not supported yet; planned for later |

Only Windows and macOS builds are currently distributed. Legacy Android tools remain in the repository for future development and are not a supported product.

## Installation

### Windows

**3.1.4 hotfix:** if 3.1.3 shows a JIT exception about EN/UK structure and revisions, install the current EXE over it. Do not delete patch state or backups. Previous 3.1.2 and 3.1.3 direct download links also serve the fixed build.

1. Download the Windows installer from [Releases](https://github.com/Braintfy/ruslocal-invokers/releases/latest).
2. Select **Ukrainian** in the game, wait for the download, then close the game and launcher completely.
3. Run the localizer, select **Check**, then install or update the translation.

**Upgrading from 3.1.2:** install **3.1.4** over the existing version once. A GitHub URL-handling bug prevents 3.1.2 from downloading translation updates; that version cannot replace its own EXE. After upgrading, Check downloads signed translation data without reinstalling the program. The [previous direct link](https://github.com/Braintfy/ruslocal-invokers/releases/download/v3.1.2-preview/InvokersRu-3.1-Preview-3.1.2-preview-win-x64.exe) remains an alias for the fixed installer; its internal version is 3.1.4.

If EN/UK changed after installation, do not manually delete state or backups. Upgrade the patcher, redownload the official Ukrainian language through the game, close the game and launcher, then check again. If it still refuses, include the check log in your report. Changed English rows without a matching translation remain English: the client version alone does not block installation.

### macOS

1. Install and launch the native **Invokers Titan Legacy** client once.
2. Select **Ukrainian** in both the official launcher and the game, wait for it to download, then quit the game completely (`Cmd+Q`). Russian replaces the Ukrainian language slot.
3. Move **Русификатор Invokers** to Applications. On first launch, right-click it and choose **Open**.
4. Select **Установить перевод**. The app displays the detected client, game version, and cache path before installation.

Do not reopen the language selector after installation: the client downloads the official file again and overwrites the translation. Reinstall the translation after a game update or language change.

The Mac localizer's **Открыть игру** button starts the native client with `uk_UA`, preventing the official launcher from reselecting English through `-language en_US`.

## What changes

The localizer composes `dl_uk_UA.bin` from the installed game files and the public translation catalog. It modifies only the user localization cache; game executables, code signature, and protection remain untouched. A verified backup is created before replacement, and **Восстановить оригинал** restores it.

Current native Mac cache:

```text
~/Library/Application Support/hitzone.anima.spirit.guardians/i18n/
```

## Development

Use the .NET SDK pinned by `global.json`.

```bash
dotnet build InvokersRu.sln -c Release
scripts/test-mac-patcher.sh
scripts/build-mac-app.sh
```

See [docs/](docs/) for technical notes and [CHANGELOG.md](CHANGELOG.md) for release history.

Build and audit the Windows patcher yourself: [English guide](docs/windows-self-build.en.md) · [Russian guide](docs/windows-self-build.ru.md). Create a translation for another language: [Community localization kit](community-localization-kit/README.md).

This is a community translation and may contain inaccuracies. Use it at your own risk.
