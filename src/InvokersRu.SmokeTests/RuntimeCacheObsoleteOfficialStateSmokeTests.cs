using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace InvokersRu.SmokeTests
{
    internal static class RuntimeCacheObsoleteOfficialStateSmokeTests
    {
        private const string Family = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa";

        internal static void Run(Action<string> passed)
        {
            // The production API correctly requires the fixed LocalAppData state path. Synthetic
            // temporary roots are only meaningful in the isolated mutation-smoke build.
            if (!MutationCapability.IsTestWriteBuild) return;
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-obsolete-official-{Guid.NewGuid():N}");
            string cache = Path.Combine(root, "cache");
            string statePath = Path.Combine(root, "state", "state.v1.json");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            try
            {
                string englishPath = Path.Combine(cache, "dl_en_US.bin");
                string targetPath = Path.Combine(cache, "dl_uk_UA.bin");
                string stampPath = Path.Combine(cache, "dl_uk_UA.bin.ver");
                byte[] english = CreateLoc1(1, "Open", "Exit");
                byte[] original = CreateLoc1(8, "Відкрити", "Вийти");
                byte[] stamp = Encoding.UTF8.GetBytes("0.60.1290");
                File.WriteAllBytes(englishPath, english);
                File.WriteAllBytes(targetPath, original);
                File.WriteAllBytes(stampPath, stamp);
                string catalogPath = Path.Combine(root, "ru_RU.jsonl");
                Loc1Document enDocument = Loc1Codec.Parse(english);
                Loc1Document baseDocument = Loc1Codec.Parse(original);
                TranslationCatalog.WriteJsonLines(catalogPath, new[]
                {
                    new TranslationRecord
                    {
                        Id = enDocument.Entries[0].Id,
                        SourceSha256 = Hashing.Sha256Text(enDocument.Entries[0].Value!),
                        HintSha256 = Hashing.Sha256Text(baseDocument.Entries[0].Value!),
                        Translation = "Открыть",
                        Status = "draft",
                        Model = "obsolete-state-smoke",
                        PromptVersion = "synthetic-v1",
                        Confidence = "high",
                        NeedsReview = false,
                        RiskFlags = Array.Empty<string>(),
                        ReviewStage = "synthetic",
                        UpdatedAt = DateTimeOffset.Parse("2026-09-24T00:00:00Z", CultureInfo.InvariantCulture)
                    }
                });
                byte[] catalog = File.ReadAllBytes(catalogPath);
                CompatibleRevisionProfileBuild built = CompatibleRevisionProfileBuilder.Build(
                    englishPath, targetPath, stampPath, Family, catalog,
                    Hashing.Sha256Bytes(catalog), "community-preview-all-drafts");
                RuntimeCacheCompatibility observed = RuntimeCacheService.DescribeTuple(
                    englishPath, targetPath, stampPath, "observed");
                VerifiedSignedUpdate signed = MakeSignedFixture(observed, built.Profile, catalog);
                RuntimeCacheCompatibility exact = SignedUpdateRuntimeProfileAdapter.SelectExact(
                    signed.Manifest, observed, baseDocument);
                Require(exact.Mode == "exact" && exact.ExpectedAppliedTranslations == 1,
                    "Synthetic signed exact profile did not describe one translation.");

                PatchState stale = NewStaleState(cache, targetPath, "C:\\missing-or-hostile\\old-backup.bin");
                byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(stale);
                File.WriteAllBytes(statePath, stateBytes);
                Require(RuntimeCacheService.Inspect(cache, exact, statePath).Status == InstallationStatus.InconsistentState,
                    "Old-state fixture was unexpectedly authorized by normal backup authentication.");
                Require(RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                        cache, statePath, signed, exact, out RuntimeCacheInspection permitted)
                    && permitted.Status == InstallationStatus.PatchSupersededByOfficialUpdate,
                    "Signed exact official tuple did not authorize archiving an obsolete state without opening its backup.");
                passed("signed exact official tuple can classify an obsolete state without trusting its backup");

                File.WriteAllText(stampPath, "0.60.1291", new UTF8Encoding(false));
                Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, exact, out _), "Changed current stamp was accepted.");
                File.WriteAllBytes(stampPath, stamp);
                File.WriteAllText(targetPath, "changed", new UTF8Encoding(false));
                Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, exact, out _), "Changed current target was accepted.");
                File.WriteAllBytes(targetPath, original);
                stale.GameRoot = Path.Combine(root, "foreign-cache");
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(stale));
                Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, exact, out _), "Foreign-installation state was accepted.");
                stale.GameRoot = Path.GetFullPath(cache);
                File.WriteAllBytes(statePath, stateBytes);
                var journal = new PatchJournal { TransactionId = Guid.NewGuid().ToString("N") };
                PatchJournalStore.Save(statePath, journal);
                Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, exact, out _), "An active transaction journal was ignored.");
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                RuntimeCacheCompatibility mismatchedPins = RuntimeCacheCompatibility.Parse(
                    JsonSerializer.Serialize(exact));
                mismatchedPins.ExpectedOutputSha256 = new string('F', 64);
                mismatchedPins.Validate();
                Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, mismatchedPins, out _),
                    "A caller-provided output pin not present in the signed profile was accepted.");
                File.WriteAllBytes(statePath, new byte[64 * 1024 + 1]);
                Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, exact, out _), "Oversized state was accepted.");
                File.WriteAllBytes(statePath, stateBytes);
                string linkedState = Path.Combine(root, "state", "linked-original.json");
                File.Move(statePath, linkedState);
                bool linkCreated = false;
                try
                {
                    File.CreateSymbolicLink(statePath, linkedState);
                    linkCreated = true;
                    Require(!RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                        cache, statePath, signed, exact, out _), "Reparse state was accepted.");
                }
                catch (Exception exception) when (!linkCreated &&
                    exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // Some Windows test hosts cannot create symbolic links without developer mode.
                }
                finally
                {
                    if (linkCreated) File.Delete(statePath);
                    File.Move(linkedState, statePath);
                }
                passed("changed official pins and foreign-installation state refuse the obsolete-state route");

                MutationPolicy.BindTestRuntimePaths(cache, statePath);
                RuntimeCacheInspection inspected = permitted;
                MutationTestHooks.BeforeSupersededStateArchive = _ =>
                    File.WriteAllText(statePath, "changed", new UTF8Encoding(false));
                bool refusedChangedState = false;
                try { RuntimeCacheService.Apply(inspected, catalogPath, statePath); }
                catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
                { refusedChangedState = true; }
                finally { MutationTestHooks.BeforeSupersededStateArchive = null; }
                Require(refusedChangedState && File.ReadAllBytes(targetPath).SequenceEqual(original),
                    "State changed between inspection and lock was not refused before target mutation.");
                File.WriteAllBytes(statePath, stateBytes);
                Require(RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, exact, out inspected), "Fixture did not re-inspect after tamper test.");
                RuntimeCacheService.Apply(inspected, catalogPath, statePath);
                string historyRoot = Path.Combine(root, "state", "history", "obsolete-official");
                string[] archives = Directory.GetFiles(historyRoot, "*.json");
                Require(archives.Length == 1 && File.ReadAllBytes(archives[0]).SequenceEqual(stateBytes)
                    && Path.GetFileName(archives[0]).StartsWith(Hashing.Sha256Bytes(stateBytes), StringComparison.Ordinal)
                    && RuntimeCacheService.Inspect(cache, exact, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Fresh apply did not preserve exact old-state bytes in a content-addressed archive.");
                RuntimeCacheService.Restore(statePath, exact);
                Require(File.ReadAllBytes(targetPath).SequenceEqual(original),
                    "Restore did not recover the exact official Ukrainian base after obsolete-state apply.");
                passed("under-lock state tamper refuses, while fresh apply archives bytes and restores exact official base");
            }
            finally
            {
                MutationTestHooks.BeforeSupersededStateArchive = null;
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static PatchState NewStaleState(string cache, string targetPath, string backupPath) => new()
        {
            BuildId = "old-prod97",
            GameRoot = Path.GetFullPath(cache),
            TargetPath = Path.GetFullPath(targetPath),
            BackupPath = backupPath,
            OriginalSha256 = new string('A', 64),
            PatchedSha256 = new string('B', 64),
            TranslationsSha256 = new string('C', 64),
            AppliedTranslations = 1,
            AppliedAt = DateTimeOffset.Parse("2026-09-22T00:00:00Z", CultureInfo.InvariantCulture)
        };

        private static VerifiedSignedUpdate MakeSignedFixture(
            RuntimeCacheCompatibility source, RuntimeCacheCompatibility composed, byte[] catalog)
        {
            string keyset = SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(
                Loc1Codec.Parse(CreateLoc1(8, "Відкрити", "Вийти")));
            var profile = new SignedUpdateCompatibilityProfile
            {
                ProfileId = "official-exact-1290",
                Mode = "exact",
                GameVersion = source.GameVersion,
                StampSha256 = source.StampSha256,
                StampValue = source.StampValue,
                ContentGuid = source.ContentGuid,
                Loc1Schema = 4,
                OrderedKeysetSha256 = keyset,
                English = new SignedUpdateCorpusIdentity
                {
                    Sha256 = source.EnglishSha256,
                    ContentVersion = source.EnglishContentVersion,
                    LocaleId = 1,
                    LocaleRevisionHex = source.EnglishLocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                    ReleaseRevision = source.EnglishReleaseRevision,
                    EntryCount = source.EntryCount
                },
                Base = new SignedUpdateCorpusIdentity
                {
                    Sha256 = source.BaseSha256,
                    ContentVersion = source.BaseContentVersion,
                    LocaleId = 8,
                    LocaleRevisionHex = source.BaseLocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                    ReleaseRevision = source.BaseReleaseRevision,
                    EntryCount = source.EntryCount
                },
                Composition = new SignedUpdateComposition
                {
                    AppliedRu = composed.ExpectedAppliedTranslations,
                    EnglishFallback = composed.ExpectedEnglishFallbacks,
                    BaseFallback = composed.ExpectedBaseFallbacks,
                    MissingCatalog = composed.ExpectedEnglishFallbacks,
                    NeedsReviewFallback = 0,
                    OutputRawSha256 = composed.ExpectedOutputSha256!
                }
            };
            var manifest = new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = 1,
                ReleaseId = "obsolete-state-smoke",
                IssuedUtc = "2026-09-24T00:00:00Z",
                ExpiresUtc = "2026-09-25T00:00:00Z",
                Patcher = new SignedUpdatePatcher(),
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = "fixture",
                    Url = "https://example.invalid/fixture",
                    Compression = "brotli",
                    CompressedBytes = 1,
                    CompressedSha256 = new string('D', 64),
                    UncompressedBytes = catalog.Length,
                    UncompressedSha256 = Hashing.Sha256Bytes(catalog),
                    RecordCount = 1,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = "validated-preview-v1"
                },
                Compatibility = new[] { profile },
                RevokedReleaseIds = Array.Empty<string>()
            };
            return new VerifiedSignedUpdate(
                new SignedUpdateEnvelope(), manifest, new string('E', 64),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), false, true,
                SignedUpdatePatcherDisposition.Current, Array.Empty<SignedUpdateWarningCode>());
        }

        private static byte[] CreateLoc1(uint localeId, params string[] values)
        {
            const int headerSize = 192;
            byte[] family = Encoding.UTF8.GetBytes(Family);
            byte[] version = Encoding.UTF8.GetBytes("Prod_0.60.0_98");
            byte[][] encoded = values.Select(Encoding.UTF8.GetBytes).ToArray();
            int dataOffset = headerSize + values.Length * 16;
            byte[] raw = new byte[dataOffset + encoded.Sum(value => value.Length)];
            Encoding.ASCII.GetBytes("LOC1").CopyTo(raw, 0);
            Write32(raw, 0x04, 4);
            Write32(raw, 0x08, localeId);
            Write32(raw, 0x0C, 98);
            Write32(raw, 0x10, localeId);
            Write32(raw, 0x1C, checked((uint)values.Length));
            Write64(raw, 0x20, headerSize);
            Write64(raw, 0x28, checked((ulong)dataOffset));
            Write64(raw, 0x30, checked((ulong)(raw.Length - dataOffset)));
            Write64(raw, 0x40, checked((ulong)dataOffset));
            Write64(raw, 0x48, checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x50, 2), checked((ushort)family.Length));
            family.CopyTo(raw, 0x52);
            int versionOffset = 0x52 + family.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(versionOffset, 2), checked((ushort)version.Length));
            version.CopyTo(raw, versionOffset + 2);
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
