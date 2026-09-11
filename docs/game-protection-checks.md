# Game protection preflight

Windows 3.1.7 and Mac script 2.9.0 pause translation installation when known local protection markers are present. This is a precaution against changing guarded files, not an anti-cheat bypass or a promise about account bans.

## Scope

- Inspect the selected localization cache for language-file signature, checksum, HMAC and integrity-manifest sidecars. Existing `.ver` and `.src` metadata remain supported.
- Inspect discovered game installations for known Easy Anti-Cheat, BattlEye, EQU8, XIGNCODE, AntiCheatExpert and GameGuard component names. Other games and unrelated system services are not scanned or disabled. Standard macOS application signing is not treated as an anti-cheat signal.
- Refuse installation if a required filesystem scan fails. Windows limits traversal size/depth and rejects redirected paths. Mac does not follow symlinks out of the game bundle.
- Repeat the check after preparation, immediately before replacing the localization file. The Core write boundary enforces the Windows check even without the GUI.
- Preserve verified restoration and interrupted-operation recovery with the existing game-closed, hash and backup checks. No automatic process termination or game launch is added.

The existing LOC1 parser separately rejects unsupported formats and incompatible structures. A higher game or translation revision by itself is not a protection signal. Compatible translation rows still match current English text and Ukrainian context before composition.

Windows support JSON schema 4 includes `protection_check.status`, `checked_paths` and `evidence`. `no-known-markers` means only that the listed paths contained none of the recognized markers. If no installation can be discovered, only the cache is checked. Mac reports the matching file in its local log. No contents are uploaded automatically.

## Limits and response

Names can be changed, protections can be embedded into existing binaries, and servers can verify account behavior or language-file hashes without local marker files. Files can also change after a scan. These checks cannot establish developer permission or eliminate ban risk.

If the patcher pauses, do not remove the marker, disable anti-cheat or force installation. Close the game, restore a verified original if offered, and provide the diagnostic details to the maintainer. If the developers explicitly prohibit unofficial localization, do not apply it. Maintainers can require a newer Windows patcher through the signed data channel; older binaries cannot acquire new code checks from translation text alone.
