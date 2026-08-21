using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Updates;
using InvokersRu.Cli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace InvokersRu.SmokeTests
{
    internal static class SignedUpdateRuntimeProfileAdapterSmokeTests
    {
        private const string AdapterOnlyArgument = "--signed-profile-adapter-only";
        private const string ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa";
        private const string GameVersion = "0.60.1247";
        private const string ContentVersion = "Prod_0.60.0_68";
        private static int _passed;

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!HasArgument(AdapterOnlyArgument)) return;

            try
            {
                Run();
                Console.WriteLine($"PASS: {_passed} signed runtime-profile adapter smoke checks");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: signed runtime-profile adapter smoke checks: {exception}");
                Environment.Exit(1);
            }
        }

        private static void Run()
        {
            Loc1Document target = CreateTarget();
            string digest = SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(target);
            string independentDigest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
                "0000000000000001\n00000000000000AB\n0123456789ABCDEF\nFFFFFFFFFFFFFFFF\n")));
            Require(digest == independentDigest, "Ordered keyset digest did not use exact ID_HEX16+LF bytes in target order.");
            Pass();

            SignedUpdateCompatibilityProfile sourceProfile = CreateProfile("runtime-cache-win64-0.60.1247-prod68", digest);
            VerifiedSignedUpdateManifest manifest = VerifySnapshot(CreateManifest(sourceProfile));
            VerifiedSignedUpdateCompatibilityProfile verifiedProfile = manifest.Compatibility[0];
            RuntimeCacheCompatibility adapted = SignedUpdateRuntimeProfileAdapter.AdaptExact(manifest, verifiedProfile, target);
            Require(adapted.Certified
                && adapted.Readiness == "ready"
                && adapted.BlockedReason == null
                && adapted.Id == sourceProfile.ProfileId
                && adapted.ContentGuid == ContentGuid
                && adapted.EnglishLocaleId == 1
                && adapted.BaseLocaleId == 8
                && adapted.EnglishLocaleRevision == 0x1234ABCD
                && adapted.BaseLocaleRevision == 0xD7A0FEFB
                && adapted.EnglishReleaseRevision == 68
                && adapted.BaseReleaseRevision == 68
                && adapted.EnglishContentVersion == ContentVersion
                && adapted.BaseContentVersion == ContentVersion
                && adapted.EntryCount == 4
                && adapted.MinimumAppliedTranslations == 2
                && adapted.ExpectedAppliedTranslations == 2
                && adapted.ExpectedEnglishFallbacks == 1
                && adapted.ExpectedBaseFallbacks == 1
                && adapted.ExpectedNeedsReviewFallbacks == 0
                && adapted.TranslationPolicy == "community-preview-all-drafts"
                && adapted.TranslationCatalogSha256 == new string('D', 64)
                && adapted.ExpectedOutputSha256 == new string('E', 64),
                "Verified exact profile was not mapped to all certified runtime pins.");
            Pass();

            adapted.SupersededArtifacts = new[]
            {
                new RuntimeCacheSupersededArtifact
                {
                    OutputSha256 = new string('7', 64),
                    TranslationCatalogSha256 = new string('8', 64),
                    AppliedTranslations = 2,
                    EnglishFallbacks = 1,
                    BaseFallbacks = 1,
                    NeedsReviewFallbacks = 0
                }
            };
            adapted.Validate();
            VerifiedSignedUpdateManifest futureManifest = VerifySnapshot(CreateManifest(
                new[]
                {
                    CreateProfile(
                        sourceProfile.ProfileId,
                        digest,
                        outputRawSha256: new string('9', 64))
                },
                translationPolicy: "validated-preview-v1",
                uncompressedCatalogSha256: new string('F', 64)));
            RuntimeCacheCompatibility future = SignedUpdateRuntimeProfileAdapter.AdaptExact(
                futureManifest,
                futureManifest.Compatibility[0],
                target);
            RuntimeCacheCompatibility bridged = RuntimeUpdateResolver.AttachEmbeddedMigrationAllowlist(adapted, future);
            Require(bridged.ExpectedOutputSha256 == new string('9', 64)
                && bridged.TranslationCatalogSha256 == new string('F', 64)
                && bridged.SupersededArtifacts.Length == 1
                && bridged.SupersededArtifacts[0].OutputSha256 == new string('7', 64)
                && bridged.SupersededArtifacts[0].TranslationCatalogSha256 == new string('8', 64),
                "A future signed artifact for the exact embedded source tuple lost the trusted bootstrap predecessor allowlist.");
            Pass();

            Require(SignedUpdateRuntimeProfileAdapter.TrySelectExact(manifest, adapted, target, out RuntimeCacheCompatibility? selected)
                && selected != null
                && selected.Id == adapted.Id,
                "Exact source-tuple selection did not return the only matching profile.");
            Pass();

            RuntimeCacheCompatibility unsupportedObserved = CloneObserved(adapted);
            unsupportedObserved.GameVersion = "0.60.1248";
            unsupportedObserved.StampValue = "0.60.1248";
            Require(!SignedUpdateRuntimeProfileAdapter.TrySelectExact(manifest, unsupportedObserved, target, out RuntimeCacheCompatibility? unsupported)
                && unsupported == null,
                "Unsupported observed tuple unexpectedly selected a signed profile.");
            Pass();

            VerifiedSignedUpdateManifest releaseManifest = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-release", digest),
                translationPolicy: "release-approved-v1"));
            RuntimeCacheCompatibility release = SignedUpdateRuntimeProfileAdapter.AdaptExact(
                releaseManifest,
                releaseManifest.Compatibility[0],
                target);
            Require(release.TranslationPolicy == "release-approved", "Release policy was not mapped strictly.");
            Pass();

            VerifiedSignedUpdateManifest wrongDigest = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-wrong-keyset", new string('A', 64))));
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.AdaptExact(
                wrongDigest,
                wrongDigest.Compatibility[0],
                target));
            Pass();

            VerifiedSignedUpdateManifest wrongSchema = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-schema3", digest, loc1Schema: 3)));
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.AdaptExact(
                wrongSchema,
                wrongSchema.Compatibility[0],
                target));
            Pass();

            VerifiedSignedUpdateManifest duplicateTuple = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-first", digest),
                CreateProfile("runtime-cache-second", digest)));
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.TrySelectExact(
                duplicateTuple,
                adapted,
                target,
                out _));
            Pass();

            VerifiedSignedUpdateManifest inconsistentCounts = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-bad-counts", digest, englishFallback: 1, missingCatalog: 0)));
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.AdaptExact(
                inconsistentCounts,
                inconsistentCounts.Compatibility[0],
                target));
            Pass();

            VerifiedSignedUpdateManifest unknownPolicy = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-policy", digest),
                translationPolicy: "future-policy-v2"));
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.AdaptExact(
                unknownPolicy,
                unknownPolicy.Compatibility[0],
                target));
            Pass();

            VerifiedSignedUpdateManifest otherManifest = VerifySnapshot(CreateManifest(
                CreateProfile("runtime-cache-foreign", digest)));
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.AdaptExact(
                manifest,
                otherManifest.Compatibility[0],
                target));
            Pass();

            var nonCanonicalEntries = new List<Loc1Entry>
            {
                new Loc1Entry(0, 2, uint.MaxValue, 0, null),
                new Loc1Entry(1, 1, uint.MaxValue, 0, null)
            };
            var nonCanonical = new Loc1Document(
                Array.Empty<byte>(), 4, 8, 68, 0xD7A0FEFB, 0, 0, ContentGuid, ContentVersion, nonCanonicalEntries);
            Expect<InvalidDataException>(() => SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(nonCanonical));
            Pass();
        }

        private static Loc1Document CreateTarget()
        {
            var entries = new List<Loc1Entry>
            {
                new Loc1Entry(0, 0x0000000000000001, uint.MaxValue, 0, null),
                new Loc1Entry(1, 0x00000000000000AB, uint.MaxValue, 0, null),
                new Loc1Entry(2, 0x0123456789ABCDEF, uint.MaxValue, 0, null),
                new Loc1Entry(3, 0xFFFFFFFFFFFFFFFF, uint.MaxValue, 0, null)
            };
            return new Loc1Document(
                Array.Empty<byte>(),
                formatVersion: 4,
                localeId: 8,
                releaseRevision: 68,
                localeRevision: 0xD7A0FEFB,
                dataOffset: 0,
                dataLength: 0,
                contentGuid: ContentGuid,
                contentVersion: ContentVersion,
                entries);
        }

        private static SignedUpdateManifest CreateManifest(
            params SignedUpdateCompatibilityProfile[] profiles)
        {
            return CreateManifest(profiles, "validated-preview-v1");
        }

        private static SignedUpdateManifest CreateManifest(
            SignedUpdateCompatibilityProfile profile,
            string translationPolicy)
        {
            return CreateManifest(new[] { profile }, translationPolicy);
        }

        private static SignedUpdateManifest CreateManifest(
            SignedUpdateCompatibilityProfile[] profiles,
            string translationPolicy,
            string? uncompressedCatalogSha256 = null)
        {
            return new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = 1,
                ReleaseId = "adapter-test",
                IssuedUtc = "2026-08-21T00:00:00Z",
                ExpiresUtc = "2026-09-01T00:00:00Z",
                Patcher = new SignedUpdatePatcher
                {
                    MinimumVersion = "3.1.0",
                    LatestVersion = "3.1.0",
                    DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
                },
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = "adapter-catalog",
                    Url = "https://github.com/Braintfy/ruslocal-invokers/releases/download/adapter-test/ru_RU.jsonl.br",
                    Compression = "brotli",
                    CompressedBytes = 100,
                    CompressedSha256 = new string('C', 64),
                    UncompressedBytes = 200,
                    UncompressedSha256 = uncompressedCatalogSha256 ?? new string('D', 64),
                    RecordCount = 2,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = translationPolicy
                },
                Compatibility = profiles,
                RevokedReleaseIds = Array.Empty<string>(),
                NotesRu = string.Empty
            };
        }

        private static SignedUpdateCompatibilityProfile CreateProfile(
            string profileId,
            string orderedKeysetSha256,
            int loc1Schema = 4,
            int englishFallback = 1,
            int missingCatalog = 1,
            string? outputRawSha256 = null)
        {
            return new SignedUpdateCompatibilityProfile
            {
                ProfileId = profileId,
                Mode = "exact",
                GameVersion = GameVersion,
                StampSha256 = new string('B', 64),
                StampValue = GameVersion,
                ContentGuid = ContentGuid,
                Loc1Schema = loc1Schema,
                OrderedKeysetSha256 = orderedKeysetSha256,
                English = new SignedUpdateCorpusIdentity
                {
                    Sha256 = new string('1', 64),
                    ContentVersion = ContentVersion,
                    LocaleId = 1,
                    LocaleRevisionHex = "1234ABCD",
                    ReleaseRevision = 68,
                    EntryCount = 4
                },
                Base = new SignedUpdateCorpusIdentity
                {
                    Sha256 = new string('2', 64),
                    ContentVersion = ContentVersion,
                    LocaleId = 8,
                    LocaleRevisionHex = "D7A0FEFB",
                    ReleaseRevision = 68,
                    EntryCount = 4
                },
                Composition = new SignedUpdateComposition
                {
                    AppliedRu = 2,
                    EnglishFallback = englishFallback,
                    BaseFallback = 1,
                    MissingCatalog = missingCatalog,
                    StaleCatalog = 0,
                    RejectedCatalog = 0,
                    NeedsReviewFallback = 0,
                    PolicyFallback = 0,
                    ValidationErrors = 0,
                    ValidationWarnings = 0,
                    OutputRawSha256 = outputRawSha256 ?? new string('E', 64)
                }
            };
        }

        private static VerifiedSignedUpdateManifest VerifySnapshot(SignedUpdateManifest source)
        {
            return new VerifiedSignedUpdateManifest(source);
        }

        private static RuntimeCacheCompatibility CloneObserved(RuntimeCacheCompatibility source)
        {
            return new RuntimeCacheCompatibility
            {
                Schema = source.Schema,
                Id = source.Id,
                GameVersion = source.GameVersion,
                ContentGuid = source.ContentGuid,
                EnglishContentVersion = source.EnglishContentVersion,
                BaseContentVersion = source.BaseContentVersion,
                EnglishSha256 = source.EnglishSha256,
                BaseSha256 = source.BaseSha256,
                StampSha256 = source.StampSha256,
                StampValue = source.StampValue,
                EnglishLocaleId = source.EnglishLocaleId,
                EnglishLocaleRevision = source.EnglishLocaleRevision,
                EnglishReleaseRevision = source.EnglishReleaseRevision,
                BaseLocaleId = source.BaseLocaleId,
                BaseLocaleRevision = source.BaseLocaleRevision,
                BaseReleaseRevision = source.BaseReleaseRevision,
                EntryCount = source.EntryCount,
                Readiness = "blocked",
                Certified = false,
                BlockedReason = "Observed test tuple.",
                MinimumAppliedTranslations = 1,
                ExpectedAppliedTranslations = 0,
                TranslationPolicy = "release-approved"
            };
        }

        private static bool HasArgument(string expected)
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, expected, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static void Expect<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Pass()
        {
            _passed++;
        }
    }
}
