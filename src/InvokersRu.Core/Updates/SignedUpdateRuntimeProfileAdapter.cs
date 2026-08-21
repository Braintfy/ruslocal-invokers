using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace InvokersRu.Core.Updates
{
    /// <summary>
    /// Converts an exact compatibility profile from an already verified update manifest into the
    /// write-capable runtime-cache profile. This is a second, deliberately narrow trust boundary:
    /// metadata that is valid for the update protocol is not automatically valid for the LOC1 writer.
    /// </summary>
    public static class SignedUpdateRuntimeProfileAdapter
    {
        private const int SupportedLoc1Schema = 4;
        private const uint EnglishLocaleId = 1;
        private const uint BaseLocaleId = 8;

        /// <summary>
        /// Computes SHA-256 over the exact ASCII byte sequence ID_HEX16+'\n' for every target entry,
        /// preserving LOC1 target order. The result is canonical uppercase hexadecimal.
        /// </summary>
        public static string ComputeOrderedKeysetSha256(Loc1Document target)
        {
            ArgumentNullException.ThrowIfNull(target);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> line = stackalloc byte[17];
            line[16] = (byte)'\n';

            ulong previous = 0;
            for (int index = 0; index < target.Entries.Count; index++)
            {
                Loc1Entry entry = target.Entries[index]
                    ?? throw new InvalidDataException("LOC1 target contains a null entry.");
                if (entry.Index != index || (index > 0 && entry.KeyHash <= previous))
                {
                    throw new InvalidDataException("LOC1 target key order is not canonical and strictly increasing.");
                }

                previous = entry.KeyHash;
                ulong value = entry.KeyHash;
                for (int digit = 15; digit >= 0; digit--)
                {
                    int nibble = (int)(value & 0xFUL);
                    line[digit] = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
                    value >>= 4;
                }

                hash.AppendData(line);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>
        /// Adapts one profile that belongs to the supplied verified manifest. All profiles are checked
        /// first so an attacker cannot hide an ambiguous duplicate beside the selected profile.
        /// </summary>
        public static RuntimeCacheCompatibility AdaptExact(
            VerifiedSignedUpdateManifest manifest,
            VerifiedSignedUpdateCompatibilityProfile signedProfile,
            Loc1Document target)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(signedProfile);
            ArgumentNullException.ThrowIfNull(target);

            ValidateManifestProfiles(manifest);
            bool belongsToManifest = false;
            foreach (VerifiedSignedUpdateCompatibilityProfile candidate in manifest.Compatibility)
            {
                if (ReferenceEquals(candidate, signedProfile))
                {
                    belongsToManifest = true;
                    break;
                }
            }

            if (!belongsToManifest)
            {
                throw new InvalidDataException("Signed compatibility profile does not belong to this verified manifest.");
            }

            ValidateTarget(signedProfile, target);
            string translationPolicy = MapTranslationPolicy(manifest.Catalog.TranslationPolicy);
            VerifiedSignedUpdateComposition composition = signedProfile.Composition;
            var adapted = new RuntimeCacheCompatibility
            {
                Schema = 1,
                Id = signedProfile.ProfileId,
                GameVersion = signedProfile.GameVersion,
                ContentGuid = signedProfile.ContentGuid,
                EnglishContentVersion = signedProfile.English.ContentVersion,
                BaseContentVersion = signedProfile.Base.ContentVersion,
                EnglishSha256 = signedProfile.English.Sha256,
                BaseSha256 = signedProfile.Base.Sha256,
                StampSha256 = signedProfile.StampSha256,
                StampValue = signedProfile.StampValue,
                EnglishLocaleId = signedProfile.English.LocaleId,
                EnglishLocaleRevision = ParseRevision(signedProfile.English.LocaleRevisionHex, "english.locale_revision_hex"),
                EnglishReleaseRevision = signedProfile.English.ReleaseRevision,
                BaseLocaleId = signedProfile.Base.LocaleId,
                BaseLocaleRevision = ParseRevision(signedProfile.Base.LocaleRevisionHex, "base.locale_revision_hex"),
                BaseReleaseRevision = signedProfile.Base.ReleaseRevision,
                EntryCount = signedProfile.Base.EntryCount,
                Readiness = "ready",
                Certified = true,
                BlockedReason = null,
                TranslationCatalogSha256 = manifest.Catalog.UncompressedSha256,
                ExpectedOutputSha256 = composition.OutputRawSha256,
                MinimumAppliedTranslations = composition.AppliedRu,
                ExpectedAppliedTranslations = composition.AppliedRu,
                ExpectedEnglishFallbacks = composition.EnglishFallback,
                ExpectedBaseFallbacks = composition.BaseFallback,
                ExpectedNeedsReviewFallbacks = composition.NeedsReviewFallback,
                TranslationPolicy = translationPolicy
            };
            adapted.Validate();
            return adapted;
        }

        /// <summary>
        /// Selects the single signed profile that exactly describes an observed local source tuple.
        /// Returns false for an unsupported game build and throws if the signed profile set is ambiguous.
        /// </summary>
        public static bool TrySelectExact(
            VerifiedSignedUpdateManifest manifest,
            RuntimeCacheCompatibility observed,
            Loc1Document target,
            out RuntimeCacheCompatibility? adapted)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(observed);
            ArgumentNullException.ThrowIfNull(target);
            ValidateManifestProfiles(manifest);
            observed.Validate();
            ValidateObservedTarget(observed, target);
            string orderedKeysetSha256 = ComputeOrderedKeysetSha256(target);

            VerifiedSignedUpdateCompatibilityProfile? match = null;
            foreach (VerifiedSignedUpdateCompatibilityProfile candidate in manifest.Compatibility)
            {
                if (!MatchesObserved(candidate, observed, orderedKeysetSha256)) continue;
                if (match != null)
                {
                    throw new InvalidDataException("Signed compatibility profile selection is ambiguous for the observed game tuple.");
                }

                match = candidate;
            }

            if (match == null)
            {
                adapted = null;
                return false;
            }

            adapted = AdaptExact(manifest, match, target);
            return true;
        }

        public static RuntimeCacheCompatibility SelectExact(
            VerifiedSignedUpdateManifest manifest,
            RuntimeCacheCompatibility observed,
            Loc1Document target)
        {
            if (!TrySelectExact(manifest, observed, target, out RuntimeCacheCompatibility? adapted))
            {
                throw new InvalidDataException("No signed exact compatibility profile matches the observed game tuple.");
            }

            return adapted!;
        }

        private static void ValidateManifestProfiles(VerifiedSignedUpdateManifest manifest)
        {
            if (manifest.Compatibility.Count is < 1 or > SignedUpdateLimits.MaxCompatibilityProfiles)
            {
                throw new InvalidDataException("Verified manifest has no bounded compatibility profile set.");
            }

            ValidateCatalog(manifest.Catalog);
            var profileIds = new HashSet<string>(StringComparer.Ordinal);
            var sourceTuples = new HashSet<SourceTuple>();
            foreach (VerifiedSignedUpdateCompatibilityProfile profile in manifest.Compatibility)
            {
                ValidateProfile(profile, manifest.Catalog);
                if (!profileIds.Add(profile.ProfileId))
                {
                    throw new InvalidDataException("Verified manifest contains duplicate compatibility profile ids.");
                }

                if (!sourceTuples.Add(SourceTuple.From(profile)))
                {
                    throw new InvalidDataException("Verified manifest contains duplicate or ambiguous exact source tuples.");
                }
            }
        }

        private static void ValidateCatalog(VerifiedSignedUpdateCatalog catalog)
        {
            if (!string.Equals(catalog.Format, "invokers-ru-jsonl-v1", StringComparison.Ordinal)
                || catalog.RecordCount is < 1 or > SignedUpdateLimits.MaxCatalogRecords)
            {
                throw new InvalidDataException("Signed catalog format or record count is unsupported by the runtime adapter.");
            }

            ValidateUpperHex(catalog.UncompressedSha256, 64, "catalog.uncompressed_sha256");
            _ = MapTranslationPolicy(catalog.TranslationPolicy);
        }

        private static void ValidateProfile(
            VerifiedSignedUpdateCompatibilityProfile profile,
            VerifiedSignedUpdateCatalog catalog)
        {
            if (!IsSafeId(profile.ProfileId)
                || !string.Equals(profile.Mode, "exact", StringComparison.Ordinal)
                || profile.Loc1Schema != SupportedLoc1Schema
                || !string.Equals(profile.GameVersion, profile.StampValue, StringComparison.Ordinal)
                || !Guid.TryParseExact(profile.ContentGuid, "D", out Guid contentGuid)
                || !string.Equals(contentGuid.ToString("D"), profile.ContentGuid, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed runtime profile is not a canonical exact LOC1 schema-4 profile.");
            }

            ValidateUpperHex(profile.StampSha256, 64, "stamp_sha256");
            ValidateUpperHex(profile.OrderedKeysetSha256, 64, "ordered_keyset_sha256");
            ValidateCorpus(profile.English, EnglishLocaleId, "english");
            ValidateCorpus(profile.Base, BaseLocaleId, "base");
            if (profile.English.EntryCount != profile.Base.EntryCount)
            {
                throw new InvalidDataException("Signed English and base corpus entry counts differ.");
            }

            ValidateComposition(profile.Composition, profile.Base.EntryCount, catalog.RecordCount, catalog.TranslationPolicy);
        }

        private static void ValidateCorpus(
            VerifiedSignedUpdateCorpusIdentity corpus,
            uint expectedLocaleId,
            string name)
        {
            if (corpus.LocaleId != expectedLocaleId
                || corpus.ReleaseRevision == 0
                || corpus.EntryCount is < 1 or > SignedUpdateLimits.MaxCatalogRecords
                || string.IsNullOrEmpty(corpus.ContentVersion)
                || corpus.ContentVersion.Length > 128)
            {
                throw new InvalidDataException($"Signed {name} corpus identity is invalid.");
            }

            foreach (char character in corpus.ContentVersion)
            {
                if (character is < (char)0x21 or > (char)0x7E)
                {
                    throw new InvalidDataException($"Signed {name} content version is not a bounded printable token.");
                }
            }

            ValidateUpperHex(corpus.Sha256, 64, $"{name}.sha256");
            uint revision = ParseRevision(corpus.LocaleRevisionHex, $"{name}.locale_revision_hex");
            if (revision == 0)
            {
                throw new InvalidDataException($"Signed {name} locale revision cannot be zero.");
            }
        }

        private static void ValidateComposition(
            VerifiedSignedUpdateComposition composition,
            int entryCount,
            int catalogRecordCount,
            string catalogPolicy)
        {
            int[] boundedCounts =
            {
                composition.AppliedRu,
                composition.EnglishFallback,
                composition.BaseFallback,
                composition.MissingCatalog,
                composition.StaleCatalog,
                composition.RejectedCatalog,
                composition.NeedsReviewFallback,
                composition.PolicyFallback,
                composition.ValidationErrors
            };
            foreach (int count in boundedCounts)
            {
                if (count < 0 || count > entryCount)
                {
                    throw new InvalidDataException("Signed composition contains an out-of-range exact count.");
                }
            }

            if (composition.AppliedRu < 1
                || composition.AppliedRu > catalogRecordCount
                || composition.ValidationErrors != 0
                || composition.ValidationWarnings is < 0 or > SignedUpdateLimits.MaxValidationWarnings
                || (long)composition.AppliedRu + composition.EnglishFallback + composition.BaseFallback != entryCount
                || (long)composition.MissingCatalog + composition.StaleCatalog + composition.RejectedCatalog
                    + composition.NeedsReviewFallback + composition.PolicyFallback != composition.EnglishFallback
                || (long)composition.AppliedRu + composition.StaleCatalog + composition.RejectedCatalog
                    + composition.NeedsReviewFallback + composition.PolicyFallback > catalogRecordCount)
            {
                throw new InvalidDataException("Signed composition pins do not exactly describe the target corpus and catalog.");
            }

            string runtimePolicy = MapTranslationPolicy(catalogPolicy);
            if ((runtimePolicy == "release-approved" || runtimePolicy == "community-preview-all-drafts")
                && (composition.NeedsReviewFallback != 0 || composition.PolicyFallback != 0))
            {
                throw new InvalidDataException("Signed composition contains fallback classes the mapped runtime policy cannot produce.");
            }

            ValidateUpperHex(composition.OutputRawSha256, 64, "composition.output_raw_sha256");
        }

        private static void ValidateTarget(
            VerifiedSignedUpdateCompatibilityProfile profile,
            Loc1Document target)
        {
            uint expectedRevision = ParseRevision(profile.Base.LocaleRevisionHex, "base.locale_revision_hex");
            if (target.FormatVersion != SupportedLoc1Schema
                || target.LocaleId != profile.Base.LocaleId
                || target.LocaleRevision != expectedRevision
                || target.ReleaseRevision != profile.Base.ReleaseRevision
                || target.Entries.Count != profile.Base.EntryCount
                || !string.Equals(target.ContentGuid, profile.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(target.ContentVersion, profile.Base.ContentVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("LOC1 target identity does not match the signed exact compatibility profile.");
            }

            string actual = ComputeOrderedKeysetSha256(target);
            if (!FixedEqualsUpperHex(actual, profile.OrderedKeysetSha256))
            {
                throw new InvalidDataException("LOC1 target ordered keyset does not match the signed compatibility pin.");
            }
        }

        private static void ValidateObservedTarget(RuntimeCacheCompatibility observed, Loc1Document target)
        {
            if (target.FormatVersion != SupportedLoc1Schema
                || target.LocaleId != observed.BaseLocaleId
                || target.LocaleRevision != observed.BaseLocaleRevision
                || target.ReleaseRevision != observed.BaseReleaseRevision
                || target.Entries.Count != observed.EntryCount
                || !string.Equals(target.ContentGuid, observed.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(target.ContentVersion, observed.BaseContentVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Observed runtime tuple and parsed LOC1 target contradict each other.");
            }
        }

        private static bool MatchesObserved(
            VerifiedSignedUpdateCompatibilityProfile profile,
            RuntimeCacheCompatibility observed,
            string orderedKeysetSha256)
        {
            return string.Equals(profile.GameVersion, observed.GameVersion, StringComparison.Ordinal)
                && string.Equals(profile.StampValue, observed.StampValue, StringComparison.Ordinal)
                && FixedEqualsUpperHex(profile.StampSha256, observed.StampSha256)
                && string.Equals(profile.ContentGuid, observed.ContentGuid, StringComparison.Ordinal)
                && FixedEqualsUpperHex(profile.OrderedKeysetSha256, orderedKeysetSha256)
                && MatchesCorpus(profile.English, observed.EnglishSha256, observed.EnglishContentVersion,
                    observed.EnglishLocaleId, observed.EnglishLocaleRevision, observed.EnglishReleaseRevision, observed.EntryCount)
                && MatchesCorpus(profile.Base, observed.BaseSha256, observed.BaseContentVersion,
                    observed.BaseLocaleId, observed.BaseLocaleRevision, observed.BaseReleaseRevision, observed.EntryCount);
        }

        private static bool MatchesCorpus(
            VerifiedSignedUpdateCorpusIdentity corpus,
            string observedSha256,
            string observedContentVersion,
            uint observedLocaleId,
            uint observedLocaleRevision,
            uint observedReleaseRevision,
            int observedEntryCount)
        {
            return FixedEqualsUpperHex(corpus.Sha256, observedSha256)
                && string.Equals(corpus.ContentVersion, observedContentVersion, StringComparison.Ordinal)
                && corpus.LocaleId == observedLocaleId
                && ParseRevision(corpus.LocaleRevisionHex, "locale_revision_hex") == observedLocaleRevision
                && corpus.ReleaseRevision == observedReleaseRevision
                && corpus.EntryCount == observedEntryCount;
        }

        private static string MapTranslationPolicy(string value)
        {
            return value switch
            {
                "release-approved-v1" => "release-approved",
                "validated-preview-v1" => "community-preview-all-drafts",
                _ => throw new InvalidDataException("Signed catalog translation policy is not supported by the runtime writer.")
            };
        }

        private static uint ParseRevision(string value, string name)
        {
            ValidateUpperHex(value, 8, name);
            if (!uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint revision))
            {
                throw new InvalidDataException($"{name} is not a canonical 32-bit hexadecimal revision.");
            }

            return revision;
        }

        private static void ValidateUpperHex(string? value, int length, string name)
        {
            if (value == null || value.Length != length)
            {
                throw new InvalidDataException($"{name} must be exactly {length} uppercase hexadecimal characters.");
            }

            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'A' && character <= 'F')))
                {
                    throw new InvalidDataException($"{name} must be exactly {length} uppercase hexadecimal characters.");
                }
            }
        }

        private static bool FixedEqualsUpperHex(string left, string right)
        {
            if (left.Length != 64 || right.Length != 64) return false;
            try
            {
                return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool IsSafeId(string value)
        {
            if (value.Length is < 1 or > 128) return false;
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_' && character != '.')
                {
                    return false;
                }
            }

            return char.IsAsciiLetterOrDigit(value[0]);
        }

        private readonly record struct SourceTuple(
            string GameVersion,
            string StampSha256,
            string StampValue,
            string ContentGuid,
            int Loc1Schema,
            string OrderedKeysetSha256,
            string EnglishSha256,
            string EnglishContentVersion,
            uint EnglishLocaleId,
            string EnglishLocaleRevisionHex,
            uint EnglishReleaseRevision,
            int EnglishEntryCount,
            string BaseSha256,
            string BaseContentVersion,
            uint BaseLocaleId,
            string BaseLocaleRevisionHex,
            uint BaseReleaseRevision,
            int BaseEntryCount)
        {
            public static SourceTuple From(VerifiedSignedUpdateCompatibilityProfile profile)
            {
                return new SourceTuple(
                    profile.GameVersion,
                    profile.StampSha256,
                    profile.StampValue,
                    profile.ContentGuid,
                    profile.Loc1Schema,
                    profile.OrderedKeysetSha256,
                    profile.English.Sha256,
                    profile.English.ContentVersion,
                    profile.English.LocaleId,
                    profile.English.LocaleRevisionHex,
                    profile.English.ReleaseRevision,
                    profile.English.EntryCount,
                    profile.Base.Sha256,
                    profile.Base.ContentVersion,
                    profile.Base.LocaleId,
                    profile.Base.LocaleRevisionHex,
                    profile.Base.ReleaseRevision,
                    profile.Base.EntryCount);
            }
        }
    }
}
