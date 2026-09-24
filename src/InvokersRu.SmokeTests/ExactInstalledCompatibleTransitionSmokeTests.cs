using InvokersRu.Cli;
using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.SmokeTests
{
    internal static class ExactInstalledCompatibleTransitionSmokeTests
    {
        private const string Family = "0.61.0";
        private const string KeyId = "exact-compatible-test-key";
        private const string EnvelopeUrl =
            "https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/update-envelope.v1.json";
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T18:00:00Z");

        internal static void Run(Action<string> passed)
        {
            RunRevision(78, changedOutput: true);
            RunRevision(84, changedOutput: true);
            passed("signed exact installations on older and newer same-family revisions can select a changed compatible catalog without weakening backup/state pins");
            RunRevision(78, changedOutput: false);
            passed("exact-to-compatible identical output remains a restorable no-op, not a false content update");
        }

        private static void RunRevision(uint installedRevision, bool changedOutput)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-exact-compatible-{Guid.NewGuid():N}");
            string cacheRoot = Path.Combine(root, "cache");
            string statePath = Path.Combine(root, "state", "state.v1.json");
            Directory.CreateDirectory(cacheRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            try
            {
                string oldStampValue = installedRevision == 78 ? "0.61.0:1123186" : "0.61.1600:1123186";
                byte[] oldEnglish = CreateLoc1(1, installedRevision, 0x11110000 + installedRevision,
                    "Open", "Exit", "Claim");
                byte[] oldBase = CreateLoc1(8, installedRevision, 0x22220000 + installedRevision,
                    "Відкрити", "Вийти", "Забрати");
                byte[] oldStamp = Encoding.UTF8.GetBytes(oldStampValue);
                byte[] currentEnglish = CreateLoc1(1, 82, 0x33330052,
                    "Open", "Exit", "Claim");
                byte[] currentBase = CreateLoc1(8, 82, 0x44440052,
                    "Відкрити", "Вийти", "Забрати");
                byte[] currentStamp = Encoding.UTF8.GetBytes("0.61.1506:1123186");

                string oldCatalogPath = Path.Combine(root, "catalog-old.jsonl");
                string currentCatalogPath = Path.Combine(root, "catalog-current.jsonl");
                WriteCatalog(oldCatalogPath, oldEnglish, oldBase, "Открыть", "old");
                WriteCatalog(currentCatalogPath, oldEnglish, oldBase,
                    changedOutput ? "Открыть сейчас" : "Открыть", "current");
                byte[] oldCatalog = File.ReadAllBytes(oldCatalogPath);
                byte[] currentCatalog = File.ReadAllBytes(currentCatalogPath);
                byte[] oldCompressed = Compress(oldCatalog);
                byte[] currentCompressed = Compress(currentCatalog);

                SignedUpdateCompatibilityProfile signedOld = SignedProfile(
                    $"signed-exact-prod{installedRevision}", oldEnglish, oldBase, oldStamp, oldCatalogPath);
                SignedUpdateCompatibilityProfile signedCurrent = SignedProfile(
                    "signed-exact-prod82", currentEnglish, currentBase, currentStamp, currentCatalogPath);
                using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                byte[] publicKey = key.ExportSubjectPublicKeyInfo();
                (byte[] oldEnvelope, VerifiedSignedUpdate oldUpdate) = Sign(
                    key, publicKey, Manifest(10, $"invokersru-data-old-{installedRevision}",
                        oldCompressed, oldCatalog, signedOld));
                (byte[] currentEnvelope, VerifiedSignedUpdate currentUpdate) = Sign(
                    key, publicKey, Manifest(11, $"invokersru-data-current-{installedRevision}",
                        currentCompressed, currentCatalog, signedCurrent));

                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schema = SignedUpdateChannelConfig.CurrentSchema,
                        kind = SignedUpdateChannelConfig.ExpectedKind,
                        envelope_url = EnvelopeUrl,
                        key_id = KeyId,
                        public_key_spki_base64 = Convert.ToBase64String(publicKey)
                    }));
                SignedUpdateStateStore updateState = (SignedUpdateStateStore)(Activator.CreateInstance(
                    typeof(SignedUpdateStateStore),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { Path.Combine(root, "updates", "state"), (Func<DateTimeOffset>)(() => Now) },
                    culture: null) ?? throw new InvalidOperationException("Could not create isolated signed state."));
                var updateCache = new SignedUpdateCacheStore(Path.Combine(root, "updates", "cache"));
                Directory.CreateDirectory(Path.Combine(root, "updates"));
                updateCache.StoreEnvelope(oldEnvelope, oldUpdate);
                updateCache.StoreCatalog(oldCompressed, oldUpdate);
                updateCache.StoreEnvelope(currentEnvelope, currentUpdate);
                updateCache.StoreCatalog(currentCompressed, currentUpdate);
                updateState.RecordAcceptedManifest(currentUpdate);
                using var http = new SignedUpdateHttpClient(new RejectUnexpectedHttp());
                using var coordinator = new SignedUpdateCoordinator(
                    config, "3.1.12", updateState, updateCache, http, () => Now);

                string englishPath = Path.Combine(cacheRoot, "dl_en_US.bin");
                string targetPath = Path.Combine(cacheRoot, "dl_uk_UA.bin");
                string stampPath = Path.Combine(cacheRoot, "uk_UA.bin.src");
                File.WriteAllBytes(englishPath, oldEnglish);
                File.WriteAllBytes(targetPath, oldBase);
                File.WriteAllBytes(stampPath, oldStamp);
                RuntimeCacheCompatibility embedded = RuntimeCacheService.DescribeTuple(
                    englishPath, targetPath, stampPath, "embedded-unrelated");
                RuntimeCacheCompatibility installed = SignedUpdateRuntimeProfileAdapter.AdaptExact(
                    oldUpdate.Manifest, oldUpdate.Manifest.Compatibility[0], Loc1Codec.Parse(oldBase));
                embedded.Readiness = "ready";
                embedded.Certified = true;
                embedded.BlockedReason = null;
                embedded.TranslationPolicy = "community-preview-all-drafts";
                embedded.TranslationCatalogSha256 = installed.TranslationCatalogSha256;
                embedded.ExpectedOutputSha256 = installed.ExpectedOutputSha256;
                embedded.MinimumAppliedTranslations = 1;
                embedded.ExpectedAppliedTranslations = installed.ExpectedAppliedTranslations;
                embedded.ExpectedEnglishFallbacks = installed.ExpectedEnglishFallbacks;
                embedded.ExpectedBaseFallbacks = installed.ExpectedBaseFallbacks;
                embedded.ExpectedNeedsReviewFallbacks = installed.ExpectedNeedsReviewFallbacks;
                embedded.Validate();

                byte[] oldPatched = Materialize(oldEnglish, oldBase, oldCatalogPath);
                Require(Hash(oldPatched) == installed.ExpectedOutputSha256,
                    "Signed historical exact profile did not pin the installed bytes.");
                string backupPath = Path.Combine(Path.GetDirectoryName(statePath)!, "backups",
                    installed.Id + "-" + Hashing.Sha256Text(installed.Id).Substring(0, 12),
                    $"{installed.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.WriteAllBytes(backupPath, oldBase);
                File.WriteAllBytes(targetPath, oldPatched);
                PatchState InstalledState() => new PatchState
                {
                    BuildId = installed.Id,
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    OriginalSha256 = installed.BaseSha256,
                    PatchedSha256 = installed.ExpectedOutputSha256!,
                    TranslationsSha256 = installed.TranslationCatalogSha256!,
                    AppliedTranslations = installed.ExpectedAppliedTranslations,
                    AppliedAt = Now
                };
                File.WriteAllText(statePath, JsonSerializer.Serialize(InstalledState()), new UTF8Encoding(false));
                Require(RuntimeCacheService.Inspect(cacheRoot, installed, statePath).Status
                    == InstallationStatus.PatchedByThisTool,
                    "Historical exact state/backup fixture is not fully authenticated.");

                RuntimeUpdateResolution selected = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, embedded, oldCatalogPath, coordinator);
                Require(selected.ChannelAuthority?.Manifest.Sequence == 11
                    && selected.InstalledProfile?.Id == installed.Id
                    && selected.InstalledInspection?.Status == InstallationStatus.PatchedByThisTool
                    && RuntimeUpdateAuthorization.CanRestoreOrRecover(selected),
                    "Current signed head lost the fully authenticated historical exact installation.");
                if (changedOutput)
                {
                    Require(selected.Profile.Mode == CompatibleRevisionProfileBuilder.Mode
                        && selected.Inspection.Status == InstallationStatus.PatchedByThisTool
                        && selected.TranslationUpdateAvailable
                        && !selected.EquivalentCatalogMetadataUpdate
                        && selected.Bundle?.Update.Manifest.Sequence == 11
                        && selected.Profile.ExpectedOutputSha256 != installed.ExpectedOutputSha256
                        && RuntimeUpdateAuthorization.CanApply(selected, Now),
                        "Signed catalog failed to select a changed compatible update from an exact installation.");
                }
                else
                {
                    Require(selected.Profile.Id == installed.Id
                        && selected.Bundle == null
                        && !selected.TranslationUpdateAvailable
                        && selected.Inspection.Status == InstallationStatus.PatchedByThisTool
                        && selected.RemoteProblem == null
                        && !RuntimeUpdateAuthorization.CanApply(selected, Now),
                        "Identical output advertised a false exact-to-compatible content update.");
                }

                byte[] originalBackup = File.ReadAllBytes(backupPath);
                File.WriteAllBytes(backupPath, currentBase);
                RuntimeUpdateResolution wrongBackup = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, embedded, oldCatalogPath, coordinator);
                Require(!wrongBackup.TranslationUpdateAvailable && wrongBackup.Bundle == null
                    && !RuntimeUpdateAuthorization.CanApply(wrongBackup, Now),
                    "A changed immutable exact backup authorized a compatible catalog transition.");
                File.WriteAllBytes(backupPath, originalBackup);

                PatchState wrongState = InstalledState();
                wrongState.BuildId = "forged-exact-build";
                File.WriteAllText(statePath, JsonSerializer.Serialize(wrongState), new UTF8Encoding(false));
                RuntimeUpdateResolution forged = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, embedded, oldCatalogPath, coordinator);
                Require(!forged.TranslationUpdateAvailable && forged.Bundle == null
                    && !RuntimeUpdateAuthorization.CanApply(forged, Now),
                    "A forged exact build id authorized a compatible catalog transition.");
                File.WriteAllText(statePath, JsonSerializer.Serialize(InstalledState()), new UTF8Encoding(false));

                File.WriteAllText(stampPath, "0.61.1700:1123186", new UTF8Encoding(false));
                RuntimeUpdateResolution wrongStamp = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, embedded, oldCatalogPath, coordinator);
                Require(!wrongStamp.TranslationUpdateAvailable && wrongStamp.Bundle == null
                    && !RuntimeUpdateAuthorization.CanApply(wrongStamp, Now),
                    "A changed current source stamp bypassed the exact installed authority.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static SignedUpdateCompatibilityProfile SignedProfile(
            string id, byte[] englishRaw, byte[] baseRaw, byte[] stampRaw, string catalogPath)
        {
            Loc1Document english = Loc1Codec.Parse(englishRaw);
            Loc1Document baseLocale = Loc1Codec.Parse(baseRaw);
            Loc1Document projected = Loc1Codec.Parse(baseRaw);
            CompositionSummary composition = TranslationComposer.Apply(
                english, projected, TranslationCatalog.LoadJsonLines(catalogPath),
                includeDraft: true, allowPerLocaleContentVersion: true, requireExactHint: true);
            byte[] output = Loc1Codec.BuildRaw(projected);
            return new SignedUpdateCompatibilityProfile
            {
                ProfileId = id,
                Mode = "exact",
                GameVersion = Encoding.UTF8.GetString(stampRaw),
                StampSha256 = Hash(stampRaw),
                StampValue = Encoding.UTF8.GetString(stampRaw),
                ContentGuid = english.ContentGuid,
                Loc1Schema = 4,
                OrderedKeysetSha256 = SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(baseLocale),
                English = Corpus(english, englishRaw),
                Base = Corpus(baseLocale, baseRaw),
                Composition = new SignedUpdateComposition
                {
                    AppliedRu = composition.AppliedTranslations,
                    EnglishFallback = composition.EnglishFallbacks,
                    BaseFallback = composition.BaseFallbacks,
                    MissingCatalog = composition.MissingCatalogRecords,
                    StaleCatalog = composition.StaleCatalogRecords,
                    RejectedCatalog = composition.RejectedCatalogRecords,
                    NeedsReviewFallback = composition.NeedsReviewFallbacks,
                    PolicyFallback = composition.PolicyFallbacks,
                    ValidationErrors = 0,
                    ValidationWarnings = 0,
                    OutputRawSha256 = Hash(output)
                }
            };
        }

        private static SignedUpdateCorpusIdentity Corpus(Loc1Document document, byte[] raw) => new()
        {
            Sha256 = Hash(raw),
            ContentVersion = document.ContentVersion,
            LocaleId = document.LocaleId,
            LocaleRevisionHex = document.LocaleRevision.ToString("X8"),
            ReleaseRevision = document.ReleaseRevision,
            EntryCount = document.Entries.Count
        };

        private static SignedUpdateManifest Manifest(
            ulong sequence, string releaseId, byte[] compressed, byte[] catalog,
            SignedUpdateCompatibilityProfile profile) => new()
        {
            Schema = 1,
            Kind = SignedUpdateVerifier.ManifestKind,
            Channel = "stable",
            Sequence = sequence,
            ReleaseId = releaseId,
            IssuedUtc = "2026-09-24T16:00:00Z",
            ExpiresUtc = "2026-10-24T16:00:00Z",
            Patcher = new SignedUpdatePatcher
            {
                MinimumVersion = "3.1.0",
                LatestVersion = "3.1.12",
                DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
            },
            Catalog = new SignedUpdateCatalog
            {
                ArtifactId = $"catalog-{sequence}",
                Url = $"https://github.com/Braintfy/ruslocal-invokers/releases/download/{releaseId}/ru_RU.jsonl.br",
                Compression = "brotli",
                CompressedBytes = compressed.LongLength,
                CompressedSha256 = Hash(compressed),
                UncompressedBytes = catalog.LongLength,
                UncompressedSha256 = Hash(catalog),
                RecordCount = 3,
                Format = "invokers-ru-jsonl-v1",
                TranslationPolicy = "validated-preview-v1"
            },
            Compatibility = new[] { profile },
            RevokedReleaseIds = Array.Empty<string>(),
            NotesRu = "Synthetic exact-to-compatible transition."
        };

        private static (byte[] Envelope, VerifiedSignedUpdate Update) Sign(
            ECDsa key, byte[] publicKey, SignedUpdateManifest manifest)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
            byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.EnvelopeKind,
                KeyId = KeyId,
                Algorithm = SignedUpdateVerifier.SignatureAlgorithm,
                PayloadBase64 = Convert.ToBase64String(payload),
                SignatureBase64 = Convert.ToBase64String(signature)
            });
            VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                envelope, publicKey, KeyId, new SignedUpdateVerificationContext(Now, "3.1.12"));
            return (envelope, verified);
        }

        private static void WriteCatalog(
            string path, byte[] englishRaw, byte[] baseRaw, string firstTranslation, string revision)
        {
            Loc1Document english = Loc1Codec.Parse(englishRaw);
            Loc1Document baseLocale = Loc1Codec.Parse(baseRaw);
            string[] translations = { firstTranslation, "Выйти", "Забрать" };
            TranslationCatalog.WriteJsonLines(path, Enumerable.Range(0, translations.Length)
                .Select(index => new TranslationRecord
                {
                    Id = english.Entries[index].Id,
                    SourceSha256 = Hashing.Sha256Text(english.Entries[index].Value!),
                    HintSha256 = Hashing.Sha256Text(baseLocale.Entries[index].Value!),
                    Translation = translations[index],
                    Status = "approved",
                    Model = "exact-compatible-smoke",
                    PromptVersion = "synthetic-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    IssueCodes = Array.Empty<string>(),
                    RiskFlags = TranslationValidator.ClassifyRisks(english.Entries[index].Value!).ToArray(),
                    ReviewStage = "synthetic",
                    ReviewerIds = new[] { "fixture" },
                    ReviewedAt = Now,
                    ReviewRevision = revision,
                    ScreenshotQa = true,
                    LegalApproved = true,
                    UpdatedAt = Now
                }));
        }

        private static byte[] Materialize(byte[] englishRaw, byte[] baseRaw, string catalogPath)
        {
            Loc1Document baseLocale = Loc1Codec.Parse(baseRaw);
            TranslationComposer.Apply(Loc1Codec.Parse(englishRaw), baseLocale,
                TranslationCatalog.LoadJsonLines(catalogPath), includeDraft: true,
                allowPerLocaleContentVersion: true, requireExactHint: true);
            return Loc1Codec.BuildRaw(baseLocale);
        }

        private static byte[] CreateLoc1(uint localeId, uint releaseRevision, uint localeRevision,
            params string[] values)
        {
            const int headerSize = 192;
            byte[] familyBytes = Encoding.UTF8.GetBytes(Family);
            byte[] versionBytes = Encoding.UTF8.GetBytes($"Prod_{Family}_{releaseRevision}");
            byte[][] encoded = values.Select(Encoding.UTF8.GetBytes).ToArray();
            int dataOffset = headerSize + values.Length * 16;
            byte[] raw = new byte[dataOffset + encoded.Sum(value => value.Length)];
            Encoding.ASCII.GetBytes("LOC1").CopyTo(raw, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x04, 4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x08, 4), localeId);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C, 4), releaseRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x10, 4), localeRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x1C, 4), checked((uint)values.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x20, 8), headerSize);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x28, 8), checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x30, 8), checked((ulong)(raw.Length - dataOffset)));
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x40, 8), checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x48, 8), checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x50, 2), checked((ushort)familyBytes.Length));
            familyBytes.CopyTo(raw, 0x52);
            int versionOffset = 0x52 + familyBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(versionOffset, 2), checked((ushort)versionBytes.Length));
            versionBytes.CopyTo(raw, versionOffset + 2);
            int offset = 0;
            for (int index = 0; index < encoded.Length; index++)
            {
                int record = headerSize + index * 16;
                BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(record, 8), checked((ulong)(index + 1)));
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(record + 8, 4), checked((uint)offset));
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(record + 12, 4), checked((uint)encoded[index].Length));
                encoded[index].CopyTo(raw, dataOffset + offset);
                offset += encoded[index].Length;
            }
            return raw;
        }

        private static byte[] Compress(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                brotli.Write(bytes);
            return output.ToArray();
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class RejectUnexpectedHttp : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                });
            }
        }
    }
}
