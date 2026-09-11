using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InvokersRu.SmokeTests
{
    internal static class GameProtectionGuardSmokeTests
    {
        internal static void Run(Action<string> passed)
        {
            WithRoot(root =>
            {
                string cache = Directory.CreateDirectory(Path.Combine(root, "i18n")).FullName;
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string unrelated = Directory.CreateDirectory(Path.Combine(root, "other-game", "EasyAntiCheat")).FullName;
                Write(Path.Combine(unrelated, "EasyAntiCheat_EOS.exe"));
                Write(Path.Combine(cache, "dl_en_US.bin"));
                Write(Path.Combine(cache, "dl_uk_UA.bin"));
                Write(Path.Combine(cache, "dl_uk_UA.bin.ver"));
                Write(Path.Combine(cache, "en_US.bin.src"));
                Write(Path.Combine(cache, "manifest.dat"));
                Write(Path.Combine(game, "Invokers.exe"));
                Write(Path.Combine(game, "README.txt"));
                Write(Path.Combine(game, "unrelated.sha256"));
                Dictionary<string, string> original = Snapshot(root);

                GameProtectionCheck clean = GameProtectionGuard.Inspect(cache, game);
                Require(!clean.BlocksApply && clean.Status == "no-known-markers" && clean.Evidence.Count == 0,
                    "Ordinary localization stamps/sources or files outside the selected game blocked installation.");
                Require(clean.CheckedPaths.Count == 2
                    && clean.CheckedPaths.Contains(cache, StringComparer.OrdinalIgnoreCase)
                    && clean.CheckedPaths.Contains(game, StringComparer.OrdinalIgnoreCase),
                    "Explicit isolated roots unexpectedly scanned another installation.");
                GameProtectionGuard.RequireAllowed(cache, game);
                RequireUnchanged(root, original);
                passed("protection preflight permits ordinary stamps and confines scans to the selected game");

                string nestedMarker = Path.Combine(game, "Invokers_Data", "Plugins", "x86_64", "EasyAntiCheat_EOS.dll");
                Write(nestedMarker);
                original = Snapshot(root);
                GameProtectionCheck protectedGame = GameProtectionGuard.Inspect(cache, game);
                Require(protectedGame.Status == "blocked" && protectedGame.BlocksApply
                    && protectedGame.Evidence.Contains(nestedMarker),
                    "A nested anti-cheat component did not block applying a translation.");
                ExpectProtectionRefusal(() => GameProtectionGuard.RequireAllowed(cache, game));
                RequireUnchanged(root, original);
                File.Delete(nestedMarker);
                passed("nested protection markers stop installation without touching detected files");

                foreach (string name in new[]
                {
                    "dl_uk_UA.bin.sig", "DL_EN_US.BIN.BR.SHA256", "uk_UA.bin.checksum",
                    "i18n.signatures.json", "localization.manifest.json",
                    "i18n.manifest.json.sig", "localization.manifest.json.sha256"
                })
                {
                    string marker = Path.Combine(cache, name);
                    Write(marker);
                    original = Snapshot(root);
                    GameProtectionCheck check = GameProtectionGuard.Inspect(cache, game);
                    Require(check.BlocksApply && check.Status == "blocked" && check.Evidence.Contains(marker),
                        $"Localization integrity marker {name} did not block applying a translation.");
                    ExpectProtectionRefusal(() => GameProtectionGuard.RequireAllowed(cache, game));
                    RequireUnchanged(root, original);
                    File.Delete(marker);
                }
                passed("localization signatures, checksums and signed manifests block installation");

                original = Snapshot(root);
                string absentGame = Path.Combine(root, "missing-game");
                GameProtectionCheck missingGame = GameProtectionGuard.Inspect(cache, absentGame);
                Require(missingGame.Status == "incomplete" && missingGame.BlocksApply
                    && missingGame.Evidence.Any(item => item.Contains(absentGame, StringComparison.Ordinal)),
                    "A missing explicitly selected game directory was reported as checked successfully.");
                string absentCache = Path.Combine(root, "missing-cache");
                GameProtectionCheck missingCache = GameProtectionGuard.Inspect(absentCache, game);
                Require(missingCache.Status == "incomplete" && missingCache.BlocksApply,
                    "A missing localization cache was reported as checked successfully.");
                ExpectProtectionRefusal(() => GameProtectionGuard.RequireAllowed(cache, absentGame));
                RequireUnchanged(root, original);
                Require(!Directory.Exists(absentGame) && !Directory.Exists(absentCache),
                    "A failed protection scan created a missing directory.");
                passed("incomplete protection scans fail closed and do not create missing paths");

                string deep = game;
                for (int depth = 0; depth < 18; depth++) deep = Path.Combine(deep, "nested");
                Directory.CreateDirectory(deep);
                GameProtectionCheck bounded = GameProtectionGuard.Inspect(cache, game);
                Require(bounded.Status == "incomplete" && bounded.BlocksApply,
                    "An incomplete depth-limited scan silently authorized installation.");
                passed("bounded protection scans refuse installation when traversal cannot finish");
            });

            if (MutationCapability.IsTestWriteBuild)
            {
                ApplyAndRestoreCheck();
                passed("direct runtime apply rechecks protection and authenticated restore stays available");
            }
        }

        private static void ApplyAndRestoreCheck()
        {
            WithRoot(root =>
            {
                string cache = Directory.CreateDirectory(Path.Combine(root, "cache")).FullName;
                string statePath = Path.Combine(root, "state", "state.v1.json");
                string catalogPath = Path.Combine(root, "translations.jsonl");
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cache);
                byte[] englishRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    1, 169, 0x12345678, new[] { "Open" }, "Prod_protection_smoke_169");
                byte[] baseRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    8, 169, 0x76543210, new[] { "Відкрити" }, "Prod_protection_smoke_169");
                File.WriteAllBytes(englishPath, englishRaw);
                File.WriteAllBytes(targetPath, baseRaw);
                File.WriteAllText(stampPath, "0.61.protection-smoke", new UTF8Encoding(false));
                Loc1Document english = Loc1Codec.Parse(englishRaw);
                TranslationCatalog.WriteJsonLines(catalogPath, new[]
                {
                    new TranslationRecord
                    {
                        Id = english.Entries[0].Id,
                        SourceSha256 = Hashing.Sha256Text("Open"),
                        HintSha256 = Hashing.Sha256Text("Відкрити"),
                        Translation = "Открыть",
                        Status = "draft",
                        Model = "protection-smoke",
                        PromptVersion = "protection-v1",
                        Confidence = "high",
                        NeedsReview = false,
                        IssueCodes = Array.Empty<string>(),
                        RiskFlags = Array.Empty<string>(),
                        UpdatedAt = DateTimeOffset.Parse("2026-09-11T00:00:00Z")
                    }
                });
                byte[] catalog = File.ReadAllBytes(catalogPath);
                CompatibleRevisionProfileBuild build = CompatibleRevisionProfileBuilder.Build(
                    englishPath, targetPath, stampPath, english.ContentGuid, catalog,
                    Hashing.Sha256Bytes(catalog), "community-preview-all-drafts");
                RuntimeCacheInspection ready = RuntimeCacheService.Inspect(cache, build.Profile, statePath);
                Require(ready.Status == InstallationStatus.CompatibleOriginal,
                    "Protection mutation fixture was not ready before the marker appeared.");

                // Simulate a protection component appearing after the caller's earlier inspection.
                string marker = targetPath + ".sig";
                Write(marker);
                Dictionary<string, string> original = Snapshot(cache);
                ExpectProtectionRefusal(() => RuntimeCacheService.Apply(ready, catalogPath, statePath));
                RequireUnchanged(cache, original);
                Require(!File.Exists(statePath) && PatchJournalStore.FindActive(statePath) == null,
                    "Blocked apply recorded an installed translation or began a transaction.");

                File.Delete(marker);
                RuntimeCacheService.Apply(RuntimeCacheService.Inspect(cache, build.Profile, statePath), catalogPath, statePath);
                Require(RuntimeCacheService.Inspect(cache, build.Profile, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Clean isolated runtime apply did not install its expected translation.");
                Write(marker);
                string markerHash = Hashing.Sha256File(marker);
                RuntimeCacheService.Restore(statePath, build.Profile);
                Require(File.ReadAllBytes(targetPath).SequenceEqual(baseRaw)
                    && File.ReadAllBytes(englishPath).SequenceEqual(englishRaw)
                    && Hashing.Sha256File(marker) == markerHash,
                    "Protection blocked exact restoration or restoration altered integrity metadata.");
            });
        }

        private static void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "isolated protection test fixture", new UTF8Encoding(false));
        }

        private static Dictionary<string, string> Snapshot(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), Hashing.Sha256File, StringComparer.Ordinal);

        private static void RequireUnchanged(string root, Dictionary<string, string> original)
        {
            Dictionary<string, string> current = Snapshot(root);
            Require(original.Count == current.Count
                && original.All(item => current.TryGetValue(item.Key, out string? hash) && hash == item.Value),
                "Protection inspection changed, removed or created files.");
        }

        private static void ExpectProtectionRefusal(Action action)
        {
            try { action(); }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("Установка перевода остановлена", StringComparison.Ordinal))
            { return; }
            throw new InvalidOperationException("Expected the protection preflight to refuse installation.");
        }

        private static void WithRoot(Action<string> action)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-protection-smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try { action(root); }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
