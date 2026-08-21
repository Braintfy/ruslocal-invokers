using InvokersRu.Core.Loc1;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace InvokersRu.Core.Patching
{
    public sealed class CompatibleRevisionProfileBuild
    {
        public required RuntimeCacheCompatibility Profile { get; init; }
        public required CompositionSummary Composition { get; init; }
        public required ValidationReport Validation { get; init; }
    }

    /// <summary>
    /// Turns one structurally compatible, locally observed official LOC1 tuple into an exact ephemeral
    /// transaction profile. The family descriptor authorizes only the known content GUID; every byte hash,
    /// composition count, ordered key set and output hash used by the writer is then pinned from this tuple.
    /// </summary>
    public static class CompatibleRevisionProfileBuilder
    {
        public const string Mode = "compatible-revision";
        private const long MaximumLoc1Bytes = 256L * 1024L * 1024L;
        private const long MaximumStampBytes = 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static CompatibleRevisionProfileBuild Build(
            string englishPath,
            string basePath,
            string stampPath,
            RuntimeCacheCompatibility trustedFamily,
            byte[] catalogBytes,
            string trustedCatalogSha256,
            string translationPolicy)
        {
            ArgumentNullException.ThrowIfNull(trustedFamily);
            trustedFamily.Validate();
            return BuildCore(
                englishPath,
                basePath,
                stampPath,
                trustedFamily.ContentGuid,
                catalogBytes,
                trustedCatalogSha256,
                translationPolicy,
                allowEmptyDiagnostic: false);
        }

        public static CompatibleRevisionProfileBuild Build(
            string englishPath,
            string basePath,
            string stampPath,
            string trustedContentGuid,
            byte[] catalogBytes,
            string trustedCatalogSha256,
            string translationPolicy)
        {
            return BuildCore(
                englishPath,
                basePath,
                stampPath,
                trustedContentGuid,
                catalogBytes,
                trustedCatalogSha256,
                translationPolicy,
                allowEmptyDiagnostic: false);
        }

        internal static CompatibleRevisionProfileBuild BuildForDiagnostics(
            string englishPath,
            string basePath,
            string stampPath,
            string trustedContentGuid,
            byte[] catalogBytes,
            string trustedCatalogSha256,
            string translationPolicy)
        {
            return BuildCore(
                englishPath,
                basePath,
                stampPath,
                trustedContentGuid,
                catalogBytes,
                trustedCatalogSha256,
                translationPolicy,
                allowEmptyDiagnostic: true);
        }

        private static CompatibleRevisionProfileBuild BuildCore(
            string englishPath,
            string basePath,
            string stampPath,
            string trustedContentGuid,
            byte[] catalogBytes,
            string trustedCatalogSha256,
            string translationPolicy,
            bool allowEmptyDiagnostic)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(trustedContentGuid);
            ArgumentNullException.ThrowIfNull(catalogBytes);
            if (catalogBytes.LongLength is < 1 or > SignedUpdateLimits.MaxUncompressedCatalogBytes)
                throw new InvalidDataException("Compatible-revision catalog size is outside the signed-update bounds.");
            if (!Guid.TryParseExact(trustedContentGuid, "D", out Guid canonicalGuid)
                || !string.Equals(canonicalGuid.ToString("D"), trustedContentGuid, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Compatible-revision family GUID is not canonical.");
            }
            ValidateTranslationPolicy(translationPolicy);

            // Snapshot every member of the observed tuple exactly once.  Parsing one generation and
            // hashing a later generation would let a concurrent launcher update produce a mixed profile.
            byte[] englishBytes = ReadBoundedFile(englishPath, MaximumLoc1Bytes, "English LOC1");
            byte[] baseBytes = ReadBoundedFile(basePath, MaximumLoc1Bytes, "base LOC1");
            byte[] stampBytes = ReadBoundedFile(stampPath, MaximumStampBytes, "version stamp");
            Loc1Document english = ParseRaw(englishBytes, "English LOC1");
            Loc1Document baseLocale = ParseRaw(baseBytes, "base LOC1");
            if (english.Entries.Count is < 1 or > 100_000 || baseLocale.Entries.Count is < 1 or > 100_000)
                throw new InvalidDataException("Compatible-revision LOC1 entry count is outside the supported bounds.");
            Loc1Compatibility.RequireComposableCorpus(english, baseLocale, allowPerLocaleContentVersion: true);
            RequireFamilyIdentity(english, baseLocale, trustedContentGuid);

            string stampValue;
            try
            {
                stampValue = StrictUtf8.GetString(stampBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Compatible-revision version stamp is not strict UTF-8.", exception);
            }

            if (!IsBareToken(stampValue, 64))
            {
                throw new InvalidDataException("Compatible-revision version stamp is not a bounded bare token.");
            }

            string catalogSha256 = Hashing.Sha256Bytes(catalogBytes);
            if (!Hashing.FixedEqualsHex(catalogSha256, trustedCatalogSha256))
            {
                throw new InvalidDataException("Compatible-revision catalog does not match its trusted SHA-256 pin.");
            }

            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(
                catalogBytes,
                SignedUpdateLimits.MaxCatalogRecords);
            bool supervisedSafeDrafts = translationPolicy == "supervised-safe-drafts";
            bool releaseApproved = translationPolicy == "release-approved";
            bool includeDraft = supervisedSafeDrafts || translationPolicy == "community-preview-all-drafts";
            ValidationReport validation = TranslationValidator.Validate(
                english,
                catalog,
                includeDraft,
                baseLocale,
                releaseApproved ? ValidationProfile.Release : ValidationProfile.Preview,
                allowPerLocaleContentVersion: true);

            CompositionSummary composition = TranslationComposer.Apply(
                english,
                baseLocale,
                catalog,
                includeDraft,
                approvedOnly: !includeDraft,
                excludeNeedsReview: supervisedSafeDrafts,
                allowPerLocaleContentVersion: true,
                eligibility: supervisedSafeDrafts
                    ? (record, source) => RuntimeSafeDraftPolicy.IsEligible(record, source, out _)
                    : releaseApproved
                        ? (record, source) => TranslationValidator.IsReleaseReady(record, source, out _)
                        : null,
                requireExactHint: true);
            bool emptyDiagnostic = composition.AppliedTranslations < 1;
            if (emptyDiagnostic && !allowEmptyDiagnostic)
            {
                throw new InvalidDataException("Compatible-revision catalog has no translation with an exact current source-and-hint match.");
            }

            string englishSha256 = Hashing.Sha256Bytes(englishBytes);
            string baseSha256 = Hashing.Sha256Bytes(baseBytes);
            string stampSha256 = Hashing.Sha256Bytes(stampBytes);
            string? outputSha256 = null;
            if (!emptyDiagnostic)
            {
                byte[] patchedRaw = Loc1Codec.BuildRawBounded(baseLocale, BoundedArtifactReader.MaximumRuntimeLoc1Bytes);
                Loc1Document reparsed = Loc1Codec.Parse(patchedRaw);
                RequireGeneratedIdentity(reparsed, baseLocale, patchedRaw.LongLength);
                outputSha256 = Hashing.Sha256Bytes(patchedRaw);
                if (Hashing.FixedEqualsHex(outputSha256, baseSha256))
                {
                    throw new InvalidDataException("Compatible-revision materialization would not change the official base locale.");
                }
            }

            string keysetSha256 = Loc1Compatibility.ComputeOrderedKeysetSha256(baseLocale);
            string identity = string.Join('\n', new[]
            {
                "compatible-revision-v1",
                stampValue,
                stampSha256,
                english.ContentGuid,
                english.ContentVersion,
                englishSha256,
                english.LocaleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                english.LocaleRevision.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                english.ReleaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                baseLocale.ContentVersion,
                baseSha256,
                baseLocale.LocaleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                baseLocale.LocaleRevision.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                baseLocale.ReleaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                baseLocale.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                keysetSha256
            });

            var profile = new RuntimeCacheCompatibility
            {
                Schema = 1,
                Id = $"runtime-cache-compatible-{Hashing.Sha256Text(identity).Substring(0, 32)}",
                Mode = Mode,
                GameVersion = stampValue,
                ContentGuid = english.ContentGuid,
                EnglishContentVersion = english.ContentVersion,
                BaseContentVersion = baseLocale.ContentVersion,
                EnglishSha256 = englishSha256,
                BaseSha256 = baseSha256,
                StampSha256 = stampSha256,
                StampValue = stampValue,
                EnglishLocaleId = english.LocaleId,
                EnglishLocaleRevision = english.LocaleRevision,
                EnglishReleaseRevision = english.ReleaseRevision,
                BaseLocaleId = baseLocale.LocaleId,
                BaseLocaleRevision = baseLocale.LocaleRevision,
                BaseReleaseRevision = baseLocale.ReleaseRevision,
                EntryCount = baseLocale.Entries.Count,
                OrderedKeysetSha256 = keysetSha256,
                Readiness = emptyDiagnostic ? "blocked" : "ready",
                Certified = !emptyDiagnostic,
                BlockedReason = emptyDiagnostic
                    ? "The trusted catalog has zero current records matching both the English source and Ukrainian hint."
                    : null,
                TranslationCatalogSha256 = catalogSha256,
                ExpectedOutputSha256 = outputSha256,
                MinimumAppliedTranslations = 1,
                ExpectedAppliedTranslations = composition.AppliedTranslations,
                ExpectedEnglishFallbacks = composition.EnglishFallbacks,
                ExpectedBaseFallbacks = composition.BaseFallbacks,
                ExpectedNeedsReviewFallbacks = composition.NeedsReviewFallbacks,
                TranslationPolicy = translationPolicy
            };
            profile.Validate();
            return new CompatibleRevisionProfileBuild
            {
                Profile = profile,
                Composition = composition,
                Validation = validation
            };
        }

        private static void RequireFamilyIdentity(
            Loc1Document english,
            Loc1Document baseLocale,
            string trustedContentGuid)
        {
            if (english.FormatVersion != 4 || baseLocale.FormatVersion != 4
                || english.LocaleId != 1 || baseLocale.LocaleId != 8
                || english.LocaleRevision == 0 || baseLocale.LocaleRevision == 0
                || english.ReleaseRevision == 0 || baseLocale.ReleaseRevision == 0
                || !string.Equals(english.ContentGuid, trustedContentGuid, StringComparison.Ordinal)
                || !string.Equals(baseLocale.ContentGuid, trustedContentGuid, StringComparison.Ordinal)
                || !IsBareToken(english.ContentVersion, 128)
                || !IsBareToken(baseLocale.ContentVersion, 128))
            {
                throw new InvalidDataException("Observed LOC1 tuple is outside the trusted compatible-revision family.");
            }
        }

        private static void RequireGeneratedIdentity(Loc1Document actual, Loc1Document source, long rawLength)
        {
            if (!HeaderIdentityMatches(actual.Header, source.Header)
                || actual.FormatVersion != 4
                || actual.DataLength != checked((ulong)rawLength - actual.DataOffset)
                || actual.LocaleId != source.LocaleId
                || actual.LocaleRevision != source.LocaleRevision
                || actual.ReleaseRevision != source.ReleaseRevision
                || !string.Equals(actual.ContentGuid, source.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(actual.ContentVersion, source.ContentVersion, StringComparison.Ordinal)
                || !actual.Entries.Select(entry => entry.KeyHash)
                    .SequenceEqual(source.Entries.Select(entry => entry.KeyHash)))
            {
                throw new InvalidDataException("Compatible-revision materialization did not preserve the exact base LOC1 identity.");
            }
        }

        private static bool HeaderIdentityMatches(byte[] actual, byte[] expected)
        {
            if (actual.Length != expected.Length) return false;
            for (int index = 0; index < actual.Length; index++)
            {
                if (index is >= 0x30 and < 0x38) continue;
                if (actual[index] != expected[index]) return false;
            }

            return true;
        }

        private static bool IsBareToken(string value, int maximumLength)
        {
            return value.Length is > 0 && value.Length <= maximumLength
                && value.All(character => character is >= (char)0x21 and <= (char)0x7E);
        }

        private static void ValidateTranslationPolicy(string value)
        {
            if (value != "release-approved"
                && value != "supervised-safe-drafts"
                && value != "community-preview-all-drafts")
            {
                throw new InvalidDataException("Compatible-revision translation policy is unsupported.");
            }
        }

        private static byte[] ReadBoundedFile(string path, long maximumBytes, string label)
        {
            return BoundedArtifactReader.ReadFile(path, maximumBytes, $"compatible-revision {label}");
        }

        private static Loc1Document ParseRaw(byte[] bytes, string label)
        {
            if (bytes.Length < 4 || bytes[0] != (byte)'L' || bytes[1] != (byte)'O'
                || bytes[2] != (byte)'C' || bytes[3] != (byte)'1')
            {
                throw new InvalidDataException($"Compatible-revision {label} is not an uncompressed raw LOC1 file.");
            }
            return Loc1Codec.Parse(bytes);
        }
    }
}
