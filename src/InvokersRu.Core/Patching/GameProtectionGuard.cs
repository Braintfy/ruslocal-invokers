using System;
using System.Collections.Generic;
using System.IO;

namespace InvokersRu.Core.Patching
{
    public sealed class GameProtectionCheck
    {
        public string Status { get; internal set; } = "no-known-markers";
        public List<string> CheckedPaths { get; } = new();
        public List<string> Evidence { get; } = new();
        public bool BlocksApply => Status != "no-known-markers";
    }

    // This is a conservative filesystem preflight, not proof of permission from the game server.
    // It never opens processes, loads game libraries, disables protection, or edits integrity metadata.
    public static class GameProtectionGuard
    {
        private const int MaximumEntries = 20000;
        private const int MaximumDepth = 16;
        private static readonly string[] IntegritySuffixes =
            { ".sig", ".signature", ".sha256", ".sha512", ".hmac", ".p7s", ".manifest", ".checksum", ".hash" };
        private static readonly string[] LocaleFiles =
            { "dl_en_us.bin", "dl_uk_ua.bin", "en_us.bin", "uk_ua.bin" };
        private static readonly string[] IntegrityManifests =
            { "i18n.manifest", "i18n.manifest.json", "i18n.signatures.json", "i18n.checksums.json",
              "localization.manifest", "localization.manifest.json", "localization.signatures.json", "localization.checksums.json" };

        public static GameProtectionCheck Inspect(string cacheRoot, string? gameRoot = null)
        {
            var result = new GameProtectionCheck();
            Scan(cacheRoot, recursive: false, result);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gameRoot != null)
            {
                roots.Add(Path.GetFullPath(gameRoot));
            }
            else if (!MutationPolicy.IsTestWriteBuild)
            {
                // Only game-specific locations; never scan the user's entire disk or other games.
                string standard = PatchPlanner.DefaultGameRoot();
                if (Directory.Exists(standard)) roots.Add(standard);
                DirectoryInfo? ancestor = Directory.GetParent(Path.GetFullPath(cacheRoot));
                for (int depth = 0; ancestor != null && depth < 4; depth++, ancestor = ancestor.Parent)
                {
                    if (File.Exists(Path.Combine(ancestor.FullName, "Invokers.exe"))) roots.Add(ancestor.FullName);
                    string child = Path.Combine(ancestor.FullName, "game");
                    if (File.Exists(Path.Combine(child, "Invokers.exe"))) roots.Add(child);
                }
            }
            foreach (string root in roots) Scan(root, recursive: true, result);
            return result;
        }

        public static void RequireAllowed(string cacheRoot, string? gameRoot = null)
        {
            GameProtectionCheck check = Inspect(cacheRoot, gameRoot);
            if (check.BlocksApply)
                throw new InvalidOperationException("Установка перевода остановлена: обнаружены признаки защиты игры "
                    + "или не удалось завершить её проверку. Не удаляйте и не отключайте защиту. "
                    + "Отправьте сведения автору и дождитесь проверки совместимости. "
                    + string.Join("; ", check.Evidence));
        }

        private static void Scan(string root, bool recursive, GameProtectionCheck result)
        {
            root = Path.GetFullPath(root);
            result.CheckedPaths.Add(root);
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            int entries = 0;
            try
            {
                PatchService.RejectExistingReparseComponents(root, "game-protection check");
                while (pending.Count > 0)
                {
                    (string path, int depth) = pending.Pop();
                    foreach (string item in Directory.EnumerateFileSystemEntries(path))
                    {
                        if (++entries > MaximumEntries)
                            throw new IOException("Превышен предел проверки каталога: " + root);
                        FileAttributes attributes = File.GetAttributes(item);
                        string name = Path.GetFileName(item).ToLowerInvariant();
                        bool directory = (attributes & FileAttributes.Directory) != 0;
                        if (IsProtectionMarker(name) || IsLocalizationIntegrityMarker(name))
                        {
                            result.Status = "blocked";
                            if (result.Evidence.Count < 8) result.Evidence.Add(item);
                        }
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Нельзя проверить ссылку или перенаправленный путь: " + item);
                        if (recursive && directory)
                        {
                            if (depth >= MaximumDepth)
                                throw new IOException("Превышена глубина проверки: " + item);
                            pending.Push((item, depth + 1));
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Security.SecurityException)
            {
                if (result.Status != "blocked") result.Status = "incomplete";
                if (result.Evidence.Count < 8)
                    result.Evidence.Add("Не удалось проверить " + root + ": " + exception.Message);
            }
        }

        private static bool IsProtectionMarker(string name)
        {
            return name.StartsWith("easyanticheat", StringComparison.Ordinal)
                || name is "battleye" or "gameguard" or "gameguard.des" or "anticheatexpert"
                || name.StartsWith("equ8", StringComparison.Ordinal)
                || name.StartsWith("xigncode", StringComparison.Ordinal)
                || name.StartsWith("ace-base", StringComparison.Ordinal)
                || name is "x3.xem" or "xhunter1.sys" or "bedaisy.sys"
                || (name.StartsWith("beservice", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal))
                || (name.StartsWith("beclient", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal))
                || name is "anticheat.dll" or "anticheat.exe" or "anticheat.sys";
        }

        private static bool IsLocalizationIntegrityMarker(string name)
        {
            foreach (string locale in LocaleFiles)
                foreach (string suffix in IntegritySuffixes)
                    if (name == locale + suffix || name == locale + ".br" + suffix
                        || name == locale + ".ver" + suffix || name == locale + ".src" + suffix)
                        return true;
            foreach (string manifest in IntegrityManifests)
            {
                if (name == manifest) return true;
                foreach (string suffix in IntegritySuffixes)
                    if (name == manifest + suffix) return true;
            }
            return false;
        }
    }
}
