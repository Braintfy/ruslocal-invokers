using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Cli;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace InvokersRu.SmokeTests
{
    internal static class RuntimeCacheSourceStampSmokeTests
    {
        private const string OldFamily = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa";
        private const string NewFamily = "0.61.0";

        public static void Run(Action<string> passed)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-source-stamp-{Guid.NewGuid():N}");
            string cache = Path.Combine(root, "cache");
            string stateRoot = Path.Combine(root, "state");
            string statePath = Path.Combine(stateRoot, "state.v1.json");
            Directory.CreateDirectory(cache);
            try
            {
                byte[] oldEnglish = CreateLoc1(OldFamily, 1, 97, 1, "Open", "Exit");
                byte[] oldBase = CreateLoc1(OldFamily, 8, 97, 2, "Відкрити", "Вийти");
                byte[] oldStamp = Encoding.UTF8.GetBytes("0.60.1289");
                string oldEnglishPath = Path.Combine(cache, "dl_en_US.bin");
                string targetPath = Path.Combine(cache, "dl_uk_UA.bin");
                string oldStampPath = Path.Combine(cache, "dl_uk_UA.bin.ver");
                File.WriteAllBytes(oldEnglishPath, oldEnglish);
                File.WriteAllBytes(targetPath, oldBase);
                File.WriteAllBytes(oldStampPath, oldStamp);
                var legacyPaths = RuntimeCacheService.ResolveTuplePaths(cache);
                Require(legacyPaths.Stamp == oldStampPath && legacyPaths.Target == targetPath,
                    "Legacy runtime-cache tuple stopped selecting dl_uk_UA.bin.ver.");
                RuntimeCacheCompatibility oldProfile = ReadyProfile(RuntimeCacheService.DescribeTuple(
                    oldEnglishPath, targetPath, oldStampPath, "old-exact"), "old-exact");

                byte[] newEnglish = CreateLoc1(NewFamily, 1, 78, 3, "Open", "Exit", "New");
                byte[] newBase = CreateLoc1(NewFamily, 8, 78, 4, "Відкрити", "Вийти", "Нове");
                string sourceStampPath = Path.Combine(cache, "uk_UA.bin.src");
                File.WriteAllBytes(oldEnglishPath, newEnglish);
                File.WriteAllBytes(targetPath, newBase);
                File.WriteAllText(sourceStampPath, "0.61.0:1123186", new UTF8Encoding(false));
                var newPaths = RuntimeCacheService.ResolveTuplePaths(cache);
                Require(newPaths.English == oldEnglishPath && newPaths.Target == targetPath
                    && newPaths.Stamp == sourceStampPath,
                    "Numeric-family runtime cache did not select the new source metadata while retaining dl target.");
                RuntimeCacheCompatibility earlierClientProfile = ReadyProfile(RuntimeCacheService.DescribeTuple(
                    newPaths.English, newPaths.Target, newPaths.Stamp, "earlier-client-exact"), "earlier-client-exact");
                Require(RuntimeCacheService.Inspect(cache, earlierClientProfile, statePath).Status
                    == InstallationStatus.CompatibleOriginal,
                    "A canonical source stamp matching the LOC1 family stopped working.");
                File.WriteAllText(sourceStampPath, "0.61.1506:1123186", new UTF8Encoding(false));
                RuntimeCacheCompatibility generatedIdProfile = RuntimeCacheService.DescribeTuple(
                    newPaths.English, newPaths.Target, newPaths.Stamp, id: null);
                Require(generatedIdProfile.Id.StartsWith("runtime-cache-source-", StringComparison.Ordinal)
                    && !generatedIdProfile.Id.Contains(':')
                    && generatedIdProfile.StampValue == "0.61.1506:1123186",
                    "A canonical source stamp did not receive a deterministic safe default profile ID.");
                RuntimeCacheCompatibility newProfile = ReadyProfile(RuntimeCacheService.DescribeTuple(
                    newPaths.English, newPaths.Target, newPaths.Stamp, "new-exact"), "new-exact");
                RuntimeCacheInspection original = RuntimeCacheService.Inspect(cache, newProfile, statePath);
                Require(original.Status == InstallationStatus.CompatibleOriginal
                    && original.StampPath == sourceStampPath
                    && original.StampValue == "0.61.1506:1123186"
                    && newProfile.ContentGuid == NewFamily
                    && RuntimeCacheService.Inspect(cache, earlierClientProfile, statePath).Status
                        == InstallationStatus.UnknownBuild
                    && BoundedArtifactReader.DecodeObservedStamp(Encoding.UTF8.GetBytes("0.61.0:bad")) == null
                    && BoundedArtifactReader.DecodeObservedStamp(Encoding.UTF8.GetBytes("0.61.0:1123186:1")) == null,
                    "Canonical client-build source stamp was not independently pinned from LOC1 family.");
                Require(RuntimeUpdateResolver.TryReadObserved(cache, out RuntimeCacheCompatibility? observed,
                        out _, out string readyProblem)
                    && observed?.StampValue == "0.61.1506:1123186"
                    && readyProblem.Length == 0,
                    "Observed numeric-family tuple did not pass the resolver's read-only input check.");
                File.Delete(oldEnglishPath);
                Require(!RuntimeUpdateResolver.TryReadObserved(cache, out _, out _, out string missingEnglish)
                    && missingEnglish.Contains("dl_en_US.bin", StringComparison.Ordinal),
                    "Missing English download was not diagnosed by file name.");
                File.WriteAllBytes(oldEnglishPath, newEnglish);
                File.Delete(targetPath);
                Require(!RuntimeUpdateResolver.TryReadObserved(cache, out _, out _, out string missingBase)
                    && missingBase.Contains("dl_uk_UA.bin", StringComparison.Ordinal),
                    "Missing Ukrainian download was not diagnosed by file name.");
                File.WriteAllBytes(targetPath, newBase);
                File.Delete(sourceStampPath);
                Require(!RuntimeUpdateResolver.TryReadObserved(cache, out _, out _, out string missingSourceStamp)
                    && missingSourceStamp.Contains("uk_UA.bin.src", StringComparison.Ordinal),
                    "Missing numeric-family source stamp was mislabeled as the legacy version file.");
                File.WriteAllText(sourceStampPath, "0.61.1506:1123186", new UTF8Encoding(false));
                File.WriteAllBytes(oldEnglishPath, oldEnglish);
                Require(!RuntimeUpdateResolver.TryReadObserved(cache, out _, out _, out string mixedCorpus)
                    && mixedCorpus.Contains("разным наборам", StringComparison.Ordinal),
                    "Mixed EN/UK corpora were not diagnosed as incompatible.");
                File.WriteAllBytes(oldEnglishPath, newEnglish);

                File.WriteAllText(sourceStampPath, "0.61.1507:1123186", new UTF8Encoding(false));
                RuntimeCacheCompatibility futureClientProfile = RuntimeCacheService.DescribeTuple(
                    newPaths.English, newPaths.Target, sourceStampPath, "future-client");
                Require(futureClientProfile.ContentGuid == NewFamily
                    && futureClientProfile.StampValue == "0.61.1507:1123186"
                    && RuntimeCacheService.Inspect(cache, newProfile, statePath).Status == InstallationStatus.UnknownBuild,
                    "A future canonical client build was either rejected or accepted against an old exact stamp pin.");
                File.WriteAllText(sourceStampPath, "0.61.1506:bad", new UTF8Encoding(false));
                bool malformedStampRejected = false;
                try { RuntimeCacheService.DescribeTuple(newPaths.English, newPaths.Target, sourceStampPath, "bad"); }
                catch (InvalidDataException) { malformedStampRejected = true; }
                Require(malformedStampRejected && RuntimeCacheService.Inspect(cache, newProfile, statePath).Status
                    == InstallationStatus.UnknownBuild, "Malformed source metadata was accepted.");
                Require(!RuntimeUpdateResolver.TryReadObserved(cache, out _, out _, out string malformedStampProblem)
                    && malformedStampProblem.Contains("uk_UA.bin.src", StringComparison.Ordinal),
                    "Malformed source stamp was not diagnosed by file name.");
                File.WriteAllText(sourceStampPath, "0.61.1506:1123186", new UTF8Encoding(false));

                string safeOldId = oldProfile.Id + "-" + Hashing.Sha256Text(oldProfile.Id).Substring(0, 12);
                string backup = Path.Combine(stateRoot, "backups", safeOldId,
                    $"{oldProfile.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.WriteAllBytes(backup, oldBase);
                Directory.CreateDirectory(stateRoot);
                var state = new PatchState
                {
                    BuildId = oldProfile.Id,
                    GameRoot = Path.GetFullPath(cache),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backup),
                    OriginalSha256 = oldProfile.BaseSha256,
                    PatchedSha256 = oldProfile.ExpectedOutputSha256!,
                    TranslationsSha256 = oldProfile.TranslationCatalogSha256!,
                    AppliedTranslations = oldProfile.ExpectedAppliedTranslations,
                    AppliedAt = DateTimeOffset.Parse("2026-09-12T12:00:00Z", CultureInfo.InvariantCulture)
                };
                File.WriteAllText(statePath, JsonSerializer.Serialize(state), new UTF8Encoding(false));
                RuntimeCacheInspection superseded = RuntimeCacheService.Inspect(cache, newProfile, statePath, oldProfile);
                Require(superseded.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && superseded.OfficialUpdatePredecessor?.Id == oldProfile.Id,
                    "Fully authenticated old-family state was not superseded by new exact official tuple.");
                File.WriteAllText(backup, "tampered", new UTF8Encoding(false));
                Require(RuntimeCacheService.Inspect(cache, newProfile, statePath, oldProfile).Status
                    == InstallationStatus.InconsistentState,
                    "Cross-family transition accepted a tampered immutable predecessor backup.");
                File.WriteAllBytes(backup, oldBase);
                if (MutationCapability.IsTestWriteBuild)
                    ApplyCrossFamilyFixture(cache, statePath, oldEnglishPath, targetPath,
                        sourceStampPath, backup, newEnglish, newBase, oldBase, newProfile, oldProfile);
                passed("source-stamp client version is independent of LOC1 family while exact pins, tamper checks, and cross-family predecessor remain enforced");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void ApplyCrossFamilyFixture(
            string cache, string statePath, string englishPath, string targetPath,
            string sourceStampPath, string oldBackupPath, byte[] newEnglish, byte[] newBase,
            byte[] oldBase, RuntimeCacheCompatibility newProfile,
            RuntimeCacheCompatibility oldProfile)
        {
            string catalogPath = Path.Combine(Path.GetDirectoryName(cache)!, "ru_RU.jsonl");
            Loc1Document english = Loc1Codec.Parse(newEnglish);
            Loc1Document baseLocale = Loc1Codec.Parse(newBase);
            TranslationCatalog.WriteJsonLines(catalogPath, new[]
            {
                new TranslationRecord
                {
                    Id = english.Entries[0].Id,
                    SourceSha256 = Hashing.Sha256Text(english.Entries[0].Value!),
                    HintSha256 = Hashing.Sha256Text(baseLocale.Entries[0].Value!),
                    Translation = "Открыть",
                    Status = "draft",
                    Model = "source-stamp-smoke",
                    PromptVersion = "synthetic-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = Array.Empty<string>(),
                    ReviewStage = "synthetic",
                    UpdatedAt = DateTimeOffset.Parse("2026-09-24T00:00:00Z", CultureInfo.InvariantCulture)
                }
            });
            byte[] catalogBytes = File.ReadAllBytes(catalogPath);
            string catalogSha256 = Hashing.Sha256Bytes(catalogBytes);
            CompatibleRevisionProfileBuild compatible = CompatibleRevisionProfileBuilder.Build(
                englishPath, targetPath, sourceStampPath, NewFamily,
                catalogBytes, catalogSha256, "community-preview-all-drafts");
            Require(compatible.Profile.StampValue == "0.61.1506:1123186"
                && compatible.Profile.ContentGuid == NewFamily
                && compatible.Composition.AppliedTranslations == 1,
                "Authenticated compatible revision rejected a canonical client-build source stamp.");
            File.WriteAllText(sourceStampPath, "0.61.1506:bad", new UTF8Encoding(false));
            bool malformedCompatibleStampRejected = false;
            try
            {
                CompatibleRevisionProfileBuilder.Build(
                    englishPath, targetPath, sourceStampPath, NewFamily,
                    catalogBytes, catalogSha256, "community-preview-all-drafts");
            }
            catch (InvalidDataException) { malformedCompatibleStampRejected = true; }
            File.WriteAllText(sourceStampPath, "0.61.1506:1123186", new UTF8Encoding(false));
            Require(malformedCompatibleStampRejected,
                "Compatible revision materialized malformed source metadata.");
            TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(catalogPath);
            CompositionSummary composition = TranslationComposer.Apply(
                english, baseLocale, catalog, includeDraft: true, approvedOnly: false,
                allowPerLocaleContentVersion: true);
            newProfile.TranslationCatalogSha256 = Hashing.Sha256File(catalogPath);
            newProfile.ExpectedOutputSha256 = Hashing.Sha256Bytes(Loc1Codec.BuildRaw(baseLocale));
            newProfile.ExpectedAppliedTranslations = composition.AppliedTranslations;
            newProfile.ExpectedEnglishFallbacks = composition.EnglishFallbacks;
            newProfile.ExpectedBaseFallbacks = composition.BaseFallbacks;
            newProfile.ExpectedNeedsReviewFallbacks = composition.NeedsReviewFallbacks;
            newProfile.Validate();
            Require(composition.AppliedTranslations == 1, "Synthetic source-stamp composition changed.");

            MutationPolicy.BindTestRuntimePaths(cache, statePath);
            byte[] originalState = File.ReadAllBytes(statePath);
            byte[] originalEnglish = File.ReadAllBytes(englishPath);
            byte[] originalStamp = File.ReadAllBytes(sourceStampPath);
            byte[] originalTarget = File.ReadAllBytes(targetPath);
            foreach ((string Label, string Path, byte[] Original) scenario in new[]
            {
                ("English", englishPath, originalEnglish),
                ("source metadata", sourceStampPath, originalStamp),
                ("old backup", oldBackupPath, oldBase),
                ("old state", statePath, originalState)
            })
            {
                RuntimeCacheInspection inspected = RuntimeCacheService.Inspect(cache, newProfile, statePath, oldProfile);
                Require(inspected.Status == InstallationStatus.PatchSupersededByOfficialUpdate,
                    $"{scenario.Label} tamper fixture did not start from authenticated superseded state.");
                MutationTestHooks.BeforeSupersededStateArchive = _ =>
                    File.WriteAllText(scenario.Path, "tampered", new UTF8Encoding(false));
                bool rejected = false;
                try { RuntimeCacheService.Apply(inspected, catalogPath, statePath); }
                catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException
                    or IOException or Loc1FormatException)
                { rejected = true; }
                finally
                {
                    MutationTestHooks.BeforeSupersededStateArchive = null;
                    File.WriteAllBytes(scenario.Path, scenario.Original);
                }
                Require(rejected && File.ReadAllBytes(targetPath).SequenceEqual(originalTarget)
                    && File.ReadAllBytes(statePath).SequenceEqual(originalState),
                    $"{scenario.Label} tamper before archive mutated the current target or old state.");
            }

            RuntimeCacheInspection superseded = RuntimeCacheService.Inspect(cache, newProfile, statePath, oldProfile);
            PatchApplyResult applied = RuntimeCacheService.Apply(superseded, catalogPath, statePath);
            string archive = Path.Combine(Path.GetDirectoryName(statePath)!, "history", "superseded");
            Require(Directory.Exists(archive) && Directory.GetFiles(archive, "*.json").Length == 1
                && File.ReadAllBytes(Directory.GetFiles(archive, "*.json")[0]).SequenceEqual(originalState)
                && File.ReadAllBytes(oldBackupPath).SequenceEqual(oldBase)
                && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), newProfile.ExpectedOutputSha256!)
                && applied.State.BuildId == newProfile.Id
                && RuntimeCacheService.Inspect(cache, newProfile, statePath).Status == InstallationStatus.PatchedByThisTool,
                "Cross-family apply did not archive the old state and commit the new source-stamp profile.");
            RuntimeCacheService.Restore(statePath, newProfile);
            Require(!File.Exists(statePath) && File.ReadAllBytes(targetPath).SequenceEqual(newBase)
                && File.ReadAllBytes(englishPath).SequenceEqual(newEnglish)
                && File.ReadAllBytes(sourceStampPath).SequenceEqual(originalStamp),
                "Cross-family restore failed to recover the exact new dl target and source metadata tuple.");
        }

        private static RuntimeCacheCompatibility ReadyProfile(RuntimeCacheCompatibility profile, string id)
        {
            profile.Id = id;
            profile.Readiness = "ready";
            profile.Certified = true;
            profile.BlockedReason = null;
            profile.TranslationCatalogSha256 = new string(id == "old-exact" ? 'A' : 'C', 64);
            profile.ExpectedOutputSha256 = new string(id == "old-exact" ? 'B' : 'D', 64);
            profile.MinimumAppliedTranslations = 1;
            profile.ExpectedAppliedTranslations = 1;
            profile.ExpectedEnglishFallbacks = profile.EntryCount - 1;
            profile.ExpectedBaseFallbacks = 0;
            profile.ExpectedNeedsReviewFallbacks = 0;
            profile.TranslationPolicy = "community-preview-all-drafts";
            profile.Validate();
            return profile;
        }

        private static byte[] CreateLoc1(string family, uint localeId, uint releaseRevision, uint localeRevision,
            params string[] values)
        {
            const int headerSize = 192;
            byte[] familyBytes = Encoding.UTF8.GetBytes(family);
            byte[] versionBytes = Encoding.UTF8.GetBytes($"Prod_{family}_{releaseRevision}");
            byte[][] encoded = values.Select(Encoding.UTF8.GetBytes).ToArray();
            int dataLength = encoded.Sum(value => value.Length);
            int dataOffset = headerSize + values.Length * 16;
            byte[] raw = new byte[dataOffset + dataLength];
            Encoding.ASCII.GetBytes("LOC1").CopyTo(raw, 0);
            Write32(raw, 0x04, 4);
            Write32(raw, 0x08, localeId);
            Write32(raw, 0x0C, releaseRevision);
            Write32(raw, 0x10, localeRevision);
            Write32(raw, 0x1C, checked((uint)values.Length));
            Write64(raw, 0x20, headerSize);
            Write64(raw, 0x28, checked((ulong)dataOffset));
            Write64(raw, 0x30, checked((ulong)dataLength));
            Write64(raw, 0x40, checked((ulong)dataOffset));
            Write64(raw, 0x48, checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x50, 2), checked((ushort)familyBytes.Length));
            familyBytes.CopyTo(raw, 0x52);
            int versionLengthOffset = 0x52 + familyBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(versionLengthOffset, 2), checked((ushort)versionBytes.Length));
            versionBytes.CopyTo(raw, versionLengthOffset + 2);
            int offset = 0;
            for (int index = 0; index < encoded.Length; index++)
            {
                int record = headerSize + index * 16;
                Write64(raw, record, checked((ulong)(index + 1)));
                Write32(raw, record + 8, checked((uint)offset));
                Write32(raw, record + 12, checked((uint)encoded[index].Length));
                encoded[index].CopyTo(raw, dataOffset + offset);
                offset += encoded[index].Length;
            }
            return raw;
        }

        private static void Write32(byte[] bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        private static void Write64(byte[] bytes, int offset, ulong value) =>
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
