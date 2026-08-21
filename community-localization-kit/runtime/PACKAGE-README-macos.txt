COMMUNITY LOCALIZATION PACK FOR macOS

This package was built locally from the public InvokersRu source. It is not an official
InvokersRu release, is not signed by the project, and is not affiliated with HitZone Inc.

Before installation into the game:
1. Select Ukrainian in Invokers and wait for the language data to finish downloading.
2. Fully quit the game with Cmd+Q and make sure its launcher is not running.
3. Open Terminal, grant it Full Disk Access if macOS requires it, then run
   ./CommunityLocalization.command and choose Check before Install.

The localization uses the existing uk_UA slot. The patcher accepts only the exact game tuple,
catalog, and built-output hashes pinned by its creator. Missing, changed, stale, or unknown data
fails closed. Missing translated rows fall back to English.

The original uk_UA file is backed up before an atomic replacement. Use Restore in the same tool.
Keep SHA256SUMS.txt with the package when auditing it. The archive and DMG are local unsigned
self-builds; macOS may require an explicit Open action and does not treat them as notarized.

If the terminal is killed and a later run reports a stale operation.lock, first make sure no
community patcher is running. Remove only the exact operation.lock directory printed by the tool
under ~/Library/Application Support/InvokersCommunityLocalization/<pack_id>/, never backups.
