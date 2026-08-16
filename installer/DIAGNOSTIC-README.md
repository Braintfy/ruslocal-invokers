# InvokersRu Diagnostic Preview

This package is a safe interface and compatibility diagnostic for the community
Russian localization project for **Invokers: Titan Legacy**.

It is **not a working localization release**. During the first supervised runtime
test the game did not load the modified LOC1 localization package. The original
official file was restored and verified. The cause is still being investigated.

Safety state of this build:

- the GUI contains no call that applies a localization patch;
- the Apply/Install localization action is deliberately disabled;
- the installer does not launch the patcher after setup;
- the installer contains no game assets or private translation jobs;
- installation is per-user and does not require administrator privileges;
- removing the utility uses the normal Windows uninstaller;
- no external translation API or updater is connected.

The GUI may be used to test path discovery, show the detected game build and
explain whether it matches the known compatibility record. A version mismatch
must be treated as a risk of an incorrect translation; this diagnostic preview
does not offer an override.

Do not invoke the CLI `apply` command from this package. `help` and read-only
`plan` diagnostics are the only intended CLI uses until the runtime loader is
validated.

Installed location:

```text
%LOCALAPPDATA%\Programs\InvokersRu
```

This community preview is unsigned, so Windows may show a SmartScreen warning.
Verify its published SHA-256 before running it.
