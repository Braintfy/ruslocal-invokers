using InvokersRu.Core.Patching;
using System;
using System.IO;

namespace InvokersRu.SmokeTests
{
    internal static class ProcessConflictSmokeTests
    {
        internal static void Run(Action<string> passed)
        {
            string game = PatchPlanner.DefaultGameRoot();
            string unrelated = Path.Combine(Path.GetTempPath(), "other-unity-game", "UnityCrashHandler64.exe");
            Require(!PatchService.IsProcessConflict(game, "UnityCrashHandler64", unrelated, true),
                "An unrelated Unity crash handler blocked runtime-cache installation.");
            Require(!PatchService.IsProcessConflict(game, "UnityCrashHandler64", null, true),
                "An unidentifiable generic Unity helper was treated as Invokers.");
            Require(PatchService.IsProcessConflict(game, "UnityCrashHandler64", Path.Combine(game, "UnityCrashHandler64.exe"), true),
                "The actual Invokers crash handler was not blocked.");
            Require(PatchService.IsProcessConflict(game, "renamed-helper", Path.Combine(game, "helpers", "worker.exe"), true),
                "A game-owned helper was not blocked by its location.");
            Require(!PatchService.IsProcessConflict(game, "UnityCrashHandler64", Path.Combine(game + "-other", "UnityCrashHandler64.exe"), true),
                "A sibling path with a common prefix was treated as the game directory.");
            Require(!PatchService.IsProcessConflict(game, "InvokersRu.Gui", Path.Combine(Path.GetTempPath(), "InvokersRu.Gui.exe"), true),
                "The translation patcher was treated as the game.");
            passed("unrelated Unity crash helpers are ignored; actual game-owned helpers remain blocked");

            foreach (string name in new[] { "Invokers", "invokers titan legacy" })
            {
                Require(PatchService.IsProcessConflict(game, name, unrelated, true), "A relocated Invokers process was missed.");
                Require(PatchService.IsProcessConflict(game, name, null, true), "An inaccessible Invokers process was missed.");
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Require(PatchService.IsProcessConflict(game, "electron", Path.Combine(local, "Programs", "Invokers Titan Legacy", "helper.exe"), true),
                "An official launcher helper was missed.");
            Require(PatchService.IsProcessConflict(game, "updater", Path.Combine(local, "invokers-updater", "update.exe"), true),
                "An official updater was missed.");
            string custom = Path.Combine(Path.GetTempPath(), "custom-invokers");
            Require(PatchService.IsProcessConflict(custom, "UnityCrashHandler64", Path.Combine(custom, "UnityCrashHandler64.exe"), false),
                "A crash handler in a selected custom game folder was missed.");
            Require(!PatchService.IsProcessConflict(custom, "Invokers", unrelated, false),
                "A scoped legacy installation scan blocked an unrelated installation.");
            passed("process checks preserve game-name, launcher, updater and custom-installation boundaries");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
