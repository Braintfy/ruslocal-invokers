# InvokersRu installer

This directory contains the reproducible, per-user installer definition for the
future GUI patcher. The installer only deploys the pre-verified patcher payload.
It does not launch the patcher, discover or modify the game, contact the network,
or require administrator privileges.

The current installer sources are **Legacy / Diagnostic WIP**. A later
runtime-cache CLI test successfully made the game load a Russian preview from
`LocalLow`, but the GUI and installer payload have not yet been migrated to that
flow. The GUI apply action must remain disabled and no binary produced from the
current installer scripts may be presented as an operational localization.

## Why Inno Setup 6

Inno Setup provides a mature Windows wizard, Start Menu integration and a normal
uninstaller while supporting installation below `%LOCALAPPDATA%` with
`PrivilegesRequired=lowest`. It is a simpler fit than MSI/MSIX for this portable,
per-user community utility.

On 2026-08-15 the local machine was checked for Inno Setup (`ISCC.exe`), WiX,
NSIS and Windows MSIX packaging tools. No compiler was present. The only external
preparation needed on a build machine is installing the official Inno Setup 6
compiler. The repository scripts do not download anything.

## Fixed payload

The build accepts exactly these thirteen top-level files and rejects directories,
reparse points, missing files and extra files:

1. `InvokersRu.Gui.exe`
2. `InvokersRu.Cli.exe`
3. `ru_RU.mvp.jsonl`
4. `GUI-PUBLISH.json`
5. `TRUSTED-COMPATIBILITY.json`
6. `PREVIEW-BUILD-REPORT.json`
7. `TRANSLATION-AUDIT.json`
8. `SUPERVISED-PUBLISH.json`
9. `README.md`
10. `TEST-INSTRUCTIONS.md`
11. `LICENSE.txt`
12. `glossary.ru.json`
13. `style-guide.ru.md`

No game asset, original localization container, private translation job or
updater credential can pass this allowlist. The generated `PAYLOAD-SHA256.json`
is installed beside the applications for independent verification.

## Build flow

For a diagnostic build, stage the final GUI publish together with the audited
support package. This step requires `GUI-PUBLISH.json` to say
`mode=diagnostic-preview`, `gui_apply_enabled=false` and
`runtime_loader_validated=false`, then verifies all receipt hashes:

```powershell
.\scripts\stage-installer-payload.ps1 `
  -GuiPublishDirectory .\work\publish\gui-diagnostic `
  -SupportPackageDirectory .\work\package\supervised-preview `
  -OutputDirectory .\work\installer-input `
  -HashManifestPath .\work\installer-input.sha256.json `
  -AppVersion 0.2.0-diagnostic-preview
```

For a deliberately assembled payload, create its immutable hash manifest
directly:

```powershell
.\scripts\new-installer-input-manifest.ps1 `
  -InputDirectory .\work\installer-input `
  -ManifestPath .\work\installer-input.sha256.json `
  -AppVersion 0.3.0-preview
```

Verify it without requiring Inno Setup:

```powershell
.\scripts\build-installer.ps1 `
  -InputDirectory .\work\installer-input `
  -HashManifest .\work\installer-input.sha256.json `
  -AppVersion 0.3.0-preview `
  -VerifyOnly
```

After Inno Setup 6 is available, build into `work`:

```powershell
.\scripts\build-installer.ps1 `
  -InputDirectory .\work\installer-input `
  -HashManifest .\work\installer-input.sha256.json `
  -AppVersion 0.3.0-preview `
  -OutputDirectory .\work\installer-output
```

The build script re-verifies every length and SHA-256, copies the payload into a
fresh staging directory, verifies the staged copy, compiles from that stage and
checks the stage again after compilation. It writes a SHA-256 sidecar for the
resulting installer. Existing installer outputs are never overwritten.

Installation target:

```text
%LOCALAPPDATA%\Programs\InvokersRu
```

The installer creates one Start Menu shortcut and registers a normal per-user
uninstaller. There is intentionally no installer `[Run]` action.
