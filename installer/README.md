# InvokersRu 3.1.1 Preview — Windows package

The Windows release is a per-user Inno Setup package around a self-contained,
multi-file `win-x64` publish. The installed player payload contains the GUI, the
supervised CLI, the exact `0.60.1247` runtime-cache profile and its pinned
translation catalog and a pinned public key for the signed data-update channel.
It contains no PowerShell, command files, separate updater executables, services
or injected game components.

The installer itself only copies files to the fixed directory
`%LOCALAPPDATA%\Programs\InvokersRu`, creates one Start Menu shortcut and
registers a per-user uninstaller. It does not request elevation, launch the
patcher, close the game, contact GitHub or modify the game. The destination page
and previous-directory reuse are disabled; a pre-install guard rejects `/DIR`
overrides, known Invokers game/cache roots, and existing junction/symlink path
components, including during silent installs.

## Prerequisites

- Windows 10 x64 build 14393 (version 1607, including Enterprise 2016 LTSC) or
  later, or Windows 11 x64;
- .NET 10 SDK for building (the resulting player does not need a system .NET);
- official Inno Setup 6.3 or newer only when compiling the installer;
- optionally, an Authenticode code-signing identity and an RFC 3161 timestamp
  service. The current 3.1 community preview is intentionally unsigned; signing
  remains the recommended next release-hardening step.

The installer and the self-contained player no longer impose the previous
Windows 10 1809 floor. Microsoft support and security updates still follow the
lifecycle of the user's Windows edition; this package cannot extend an
end-of-support operating system's lifecycle.

The pinned .NET 10 SDK exposes Windows API reference packs down to 17763, so the
GUI compiles against that oldest available pack while
`SupportedOSPlatformVersion` and Setup both declare the actual 14393 runtime
floor. The application uses only APIs available at that declared floor; the
target-pack number is not an installer or runtime version check.

Players using the already published 3.1.0 preview must install 3.1.1 once. The
Windows 10 floor and compatible-revision behavior are executable code changes,
not translation data. After that migration, ordinary compatible catalog
updates continue through the signed data channel unless the application itself
explicitly reports that a newer patcher is required.

The scripts do not download Inno Setup, certificates or signing tools. During
`dotnet restore`, the publish script uses the official NuGet v3 feed only to
obtain the matching Microsoft `win-x64` runtime packs when they are not already
available. `-NoRestore` makes an already-restored build fully offline.

## 1. Publish the verified player payload

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-windows-preview.ps1 `
  -OutputDirectory .\work\publish\windows-3.1.1-preview `
  -AppVersion 3.1.1-preview `
  -SignedUpdateChannelConfig .\config\signed-update-channel.v1.json
```

If `dotnet` on `PATH` is older than .NET 10, add
`-DotNetPath C:\path\to\dotnet-10\dotnet.exe`. The script checks the selected SDK
before restoring or compiling anything.

The script performs these checks before creating the final directory:

- the catalog SHA-256 equals the certified `0.60.1247` profile pin;
- the CLI is compiled with supervised mutation capability and embeds that exact
  runtime-cache profile;
- the signed-update channel URL, key ID and public-key fingerprint equal the
  reviewed public configuration and are embedded in the supervised CLI;
- GUI and CLI are self-contained, multi-file `win-x64` publishes with trimming,
  ReadyToRun and single-file compression disabled;
- the WindowsDesktop self-contained runtime is kept as one coherent superset;
  only the five exact supervised-CLI application files are merged into it, then
  the packaged CLI is executed to prove that it loads that runtime and its
  embedded profile;
- exactly `InvokersRu.Gui.exe` and `InvokersRu.Cli.exe` are present;
- the optional .NET `createdump.exe` helper is removed before hashing so the
  installed package has no third executable surface;
- scripts, shortcuts, archives and nested installers are rejected;
- every final relative path, length and SHA-256 is recorded in
  `PAYLOAD-SHA256.json`.

The output path must be new, below `work`, outside the game and free of reparse
points. The script never writes to the installed game.

An unsigned local publish is allowed for testing and prints a prominent warning.
To sign the project entry points before the payload hashes are sealed:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-windows-preview.ps1 `
  -OutputDirectory .\work\publish\windows-3.1.1-preview-signed `
  -AppVersion 3.1.1-preview `
  -SignedUpdateChannelConfig .\config\signed-update-channel.v1.json `
  -SignToolPath "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" `
  -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567 `
  -TimestampUrl https://your-rfc3161-provider.example
```

Use a real thumbprint and the timestamp URL supplied by the certificate or
Trusted Signing provider. Secrets are not stored in the repository.

## 2. Verify without compiling an installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 `
  -InputDirectory .\work\publish\windows-3.1.1-preview `
  -AppVersion 3.1.1-preview `
  -VerifyOnly
```

Verification rejects any extra, missing, changed or linked file. The manifest is
schema 2 and acts as the exact path/hash allowlist for the multi-file .NET
runtime payload. `-VerifyOnly` never validates an Inno Sign Tool and never claims
that an installer was signed. To verify an already signed player payload, add
`-ExpectedSignerThumbprint` without `-InnoSignToolName`.

## 3. Compile an unsigned local installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 `
  -InputDirectory .\work\publish\windows-3.1.1-preview `
  -AppVersion 3.1.1-preview `
  -OutputDirectory .\work\installer-output
```

The output is named `InvokersRu-3.1-Preview-3.1.1-preview-win-x64.exe` and gets a
`.sha256` sidecar. The current community preview may be distributed unsigned,
but its exact source commit and SHA-256 sidecar must be published prominently.
Windows can show “Unknown publisher” or SmartScreen until a trusted certificate
and reputation are available; the data-channel ECDSA signature does not sign the
EXE and does not suppress that warning.

## 4. Compile a signed release installer

In Inno Setup, configure a named Sign Tool that invokes `signtool.exe` (or the
provider's Authenticode client) with SHA-256 and RFC 3161 timestamping. Then pass
only that configured name:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 `
  -InputDirectory .\work\publish\windows-3.1.1-preview-signed `
  -AppVersion 3.1.1-preview `
  -OutputDirectory .\work\installer-output-signed `
  -InnoSignToolName InvokersRuRelease `
  -ExpectedSignerThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

`InvokersRu.iss` applies the named hook to Setup and the generated uninstaller.
Before invoking Inno, the build independently verifies the five project binaries
and `BUILD-RECEIPT.json` against the expected thumbprint. It then fails unless
the final installer has a valid signature from that same signer. Merely passing
a configured or fake Sign Tool name is never treated as signature evidence.

Code signing materially reduces warnings, but no project can promise that every
antivirus product will always accept every new binary. Avoiding obfuscation,
packers, script launchers, elevation, injection and self-updating executables
keeps the package transparent and easier to verify.
