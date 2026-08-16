# Diagnostic preview test instructions

This test validates only the installer and GUI wrapper. It must not modify the
game or claim that Russian localization works.

1. Confirm that the game and official launcher remain unchanged.
2. Run the diagnostic installer and keep the default per-user location.
3. Confirm that an `InvokersRu Diagnostic Preview` shortcut appears in the Start
   Menu.
4. Start the GUI manually from that shortcut.
5. Confirm that it finds the installed game path or clearly explains that the
   path was not found.
6. Confirm that it shows the detected game/build version and warns when it does
   not match the supported record.
7. Confirm that the localization Apply/Install action is visibly disabled.
8. Do not invoke `InvokersRu.Cli.exe apply`. Read-only `help` and `plan` are the
   only intended CLI diagnostics for this build.
9. Capture screenshots of the path, build-status and disabled-action states for
   community review.
10. Close the GUI and remove it through Windows Installed apps. Confirm that the
    Start Menu shortcut and `%LOCALAPPDATA%\Programs\InvokersRu` application files
    are removed.

If any screen offers to apply a translation, stop testing and report the exact
package SHA-256. That would be a packaging defect.
