using InvokersRu.Cli;
using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace InvokersRu.SmokeTests
{
    internal static class CompatibleResolutionFailureSmokeTests
    {
        internal static void Run(Action<string> passed)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-resolution-failure-{Guid.NewGuid():N}");
            string cache = Path.Combine(root, "cache");
            string statePath = Path.Combine(root, "state", "state.v1.json");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            try
            {
                string englishPath = Path.Combine(cache, "dl_en_US.bin");
                string targetPath = Path.Combine(cache, "dl_uk_UA.bin");
                string stampPath = Path.Combine(cache, "dl_uk_UA.bin.ver");
                string catalogPath = Path.Combine(root, "catalog.jsonl");
                string[] en = { "Open", "Exit" };
                string[] uk = { "Відкрити", "Вийти" };
                byte[] oldEnglish = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(1, 68, 1, en);
                byte[] oldBase = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(8, 68, 2, uk);
                File.WriteAllBytes(englishPath, oldEnglish);
                File.WriteAllBytes(targetPath, oldBase);
                File.WriteAllText(stampPath, "0.60.synthetic.68", new UTF8Encoding(false));
                RuntimeCacheCompatibility family = RuntimeCacheService.DescribeTuple(
                    englishPath, targetPath, stampPath, "trusted-test-family");
                byte[] currentEnglish = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    1, 169, 3, en, "Prod_synthetic_169");
                byte[] currentBase = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    8, 169, 4, uk, "Prod_synthetic_169");
                File.WriteAllBytes(englishPath, currentEnglish);
                File.WriteAllBytes(targetPath, currentBase);
                File.WriteAllText(stampPath, "0.61.synthetic.169", new UTF8Encoding(false));
                Loc1Document enDocument = Loc1Codec.Parse(currentEnglish);
                Loc1Document ukDocument = Loc1Codec.Parse(currentBase);
                TranslationRecord Record(string source, string hint) => new TranslationRecord
                {
                    Id = enDocument.Entries[0].Id,
                    SourceSha256 = Hashing.Sha256Text(source),
                    HintSha256 = Hashing.Sha256Text(hint),
                    Translation = "Открыть",
                    Status = "draft",
                    Model = "failure-regression",
                    PromptVersion = "synthetic-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = Array.Empty<string>(),
                    ReviewStage = "synthetic",
                    UpdatedAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z")
                };
                void Catalog(TranslationRecord record)
                {
                    if (File.Exists(catalogPath)) File.Delete(catalogPath);
                    TranslationCatalog.WriteJsonLines(catalogPath, new[] { record });
                    family.TranslationCatalogSha256 = Hashing.Sha256File(catalogPath);
                    family.TranslationPolicy = "community-preview-all-drafts";
                    family.Validate();
                }
                RuntimeUpdateResolution Resolve() => RuntimeUpdateResolver.Resolve(
                    cache, statePath, family, catalogPath, coordinator: null);
                Catalog(Record(en[0], uk[0]));
                RuntimeUpdateResolution partial = Resolve();
                Require(partial.Profile.Mode == CompatibleRevisionProfileBuilder.Mode
                    && partial.Inspection.Status == InstallationStatus.CompatibleOriginal
                    && partial.Profile.ExpectedAppliedTranslations == 1
                    && partial.Profile.ExpectedEnglishFallbacks == 1
                    && partial.CompatibilityFailure == null,
                    "A newer revision with partial current catalog coverage was blocked.");
                passed("newer compatible revisions apply matching rows without requiring a new exact profile");

                Catalog(Record("Old source", uk[0]));
                RuntimeUpdateResolution empty = Resolve();
                Require(empty.LocalProblem == "catalog-no-current-matches"
                    && empty.CompatibilityFailure?.Stage == "match-records"
                    && empty.CompatibilityFailure.Composition?.AppliedTranslations == 0
                    && empty.CompatibilityFailure.Composition.StaleCatalogRecords == 1
                    && empty.CompatibilityFailure.Composition.EnglishFallbacks == 2
                    && !RuntimeUpdateAuthorization.CanApply(empty, DateTimeOffset.UtcNow),
                    "An empty current catalog lost its coverage counts or was mislabeled as a missing profile.");
                Catalog(Record(en[0], "Old Ukrainian hint"));
                RuntimeUpdateResolution staleHint = Resolve();
                Require(staleHint.LocalProblem == "catalog-no-current-matches"
                    && staleHint.CompatibilityFailure?.Composition?.StaleHintRecords == 1,
                    "A stale Ukrainian hint was not diagnosed independently of the English source.");
                passed("zero current source/hint matches report coverage counts and refuse mutation");

                Catalog(Record(en[0], uk[0]));
                File.WriteAllBytes(englishPath, Loc1Codec.Compress(currentEnglish));
                RuntimeUpdateResolution compressed = Resolve();
                Require(compressed.LocalProblem == "runtime-container-unsupported"
                    && compressed.CompatibilityFailure?.Stage == "materialize"
                    && !RuntimeUpdateAuthorization.CanApply(compressed, DateTimeOffset.UtcNow),
                    "A readable compressed container was incorrectly diagnosed as an absent signed profile.");
                File.WriteAllBytes(englishPath, currentEnglish);
                File.WriteAllText(statePath, "{broken state", new UTF8Encoding(false));
                RuntimeUpdateResolution unreadable = Resolve();
                Require(unreadable.LocalProblem == "runtime-state-unreadable"
                    && unreadable.Inspection.Status == InstallationStatus.InconsistentState
                    && unreadable.CompatibilityFailure?.Stage == "read-state"
                    && unreadable.Inspection.EnglishContentVersion == "Prod_synthetic_169",
                    "Unreadable local state was hidden by a missing-profile diagnostic.");
                File.Delete(statePath);
                passed("unsupported containers and unreadable previous state retain distinct refusal reasons");

                TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(catalogPath);
                Loc1Document patchedDocument = Loc1Codec.Parse(currentBase);
                TranslationComposer.Apply(enDocument, patchedDocument, catalog, includeDraft: true,
                    allowPerLocaleContentVersion: true, requireExactHint: true);
                byte[] patched = Loc1Codec.BuildRaw(patchedDocument);
                File.WriteAllBytes(targetPath, patched);
                var badState = new PatchState
                {
                    BuildId = "runtime-cache-legacy",
                    GameRoot = cache,
                    TargetPath = targetPath,
                    BackupPath = Path.Combine(root, "missing-backup.bin"),
                    OriginalSha256 = Hashing.Sha256Bytes(currentBase),
                    PatchedSha256 = Hashing.Sha256Bytes(patched),
                    TranslationsSha256 = Hashing.Sha256File(catalogPath),
                    AppliedTranslations = 1,
                    AppliedAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z")
                };
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(badState));
                byte[] beforeState = File.ReadAllBytes(statePath);
                RuntimeUpdateResolution badBackup = Resolve();
                Require(badBackup.LocalProblem == "runtime-backup-unavailable"
                    && badBackup.CompatibilityFailure?.Stage == "authenticate-backup"
                    && badBackup.Inspection.Status == InstallationStatus.InconsistentState
                    && !RuntimeUpdateAuthorization.CanApply(badBackup, DateTimeOffset.UtcNow)
                    && !RuntimeUpdateAuthorization.CanRestoreOrRecover(badBackup)
                    && File.ReadAllBytes(statePath).SequenceEqual(beforeState)
                    && File.ReadAllBytes(targetPath).SequenceEqual(patched),
                    "An untrusted backup was mislabeled, authorized or modified by read-only planning.");
                passed("a matching installed target with an untrusted backup remains blocked without losing its real reason");

                // The launcher can replace the patched target while leaving stale state behind.
                File.WriteAllBytes(targetPath, currentBase);
                RuntimeUpdateResolution staleState = Resolve();
                Require(staleState.LocalProblem == "runtime-state-untrusted"
                    && staleState.CompatibilityFailure?.Stage == "authenticate-state"
                    && staleState.CompatibilityFailure.Composition?.AppliedTranslations == 1
                    && staleState.Inspection.Status == InstallationStatus.InconsistentState
                    && !RuntimeUpdateAuthorization.CanApply(staleState, DateTimeOffset.UtcNow),
                    "Applicable partial translations hid an unsafe stale-state refusal as a missing profile.");
                passed("unsafe stale state reports its applicable translation count while keeping writes blocked");

                RuntimeCacheCompatibility installedExact = RuntimeCacheService.DescribeTuple(
                    englishPath, targetPath, stampPath, "current-trusted-exact");
                installedExact.Certified = true;
                installedExact.Readiness = "ready";
                installedExact.BlockedReason = null;
                installedExact.TranslationCatalogSha256 = Hashing.Sha256File(catalogPath);
                installedExact.ExpectedOutputSha256 = Hashing.Sha256Bytes(patched);
                installedExact.ExpectedAppliedTranslations = 1;
                installedExact.ExpectedEnglishFallbacks = 1;
                installedExact.ExpectedBaseFallbacks = 0;
                installedExact.ExpectedNeedsReviewFallbacks = 0;
                installedExact.TranslationPolicy = "community-preview-all-drafts";
                installedExact.Validate();
                string safeProfile = installedExact.Id + "-" + Hashing.Sha256Text(installedExact.Id).Substring(0, 12);
                string backupDirectory = Path.Combine(Path.GetDirectoryName(statePath)!, "backups", safeProfile);
                Directory.CreateDirectory(backupDirectory);
                string backupPath = Path.Combine(backupDirectory, installedExact.BaseSha256 + ".dl_uk_UA.bin");
                File.WriteAllBytes(backupPath, currentBase);
                badState.BuildId = installedExact.Id;
                badState.BackupPath = backupPath;
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(badState));
                File.WriteAllBytes(targetPath, patched);
                File.Delete(catalogPath);
                RuntimeUpdateResolution restorable = RuntimeUpdateResolver.Resolve(
                    cache, statePath, installedExact, catalogPath, coordinator: null);
                Require(restorable.Inspection.Status == InstallationStatus.PatchedByThisTool
                    && restorable.CompatibilityFailure != null
                    && RuntimeUpdateAuthorization.CanRestoreOrRecover(restorable)
                    && !RuntimeUpdateAuthorization.CanApply(restorable, DateTimeOffset.UtcNow)
                    && File.ReadAllBytes(targetPath).SequenceEqual(patched),
                    $"A catalog failure suppressed independently authenticated installed/restore authority: {restorable.Inspection.Status}; {restorable.Inspection.Message}; {restorable.LocalProblem}; restore={RuntimeUpdateAuthorization.CanRestoreOrRecover(restorable)}; apply={RuntimeUpdateAuthorization.CanApply(restorable, DateTimeOffset.UtcNow)}.");
                passed("authenticated installed translations stay restorable when catalog preparation fails");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
