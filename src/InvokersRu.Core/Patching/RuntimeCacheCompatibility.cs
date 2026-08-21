using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Patching
{
    public sealed class RuntimeCacheCompatibility
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; } = 1;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("game_version")]
        public string GameVersion { get; set; } = string.Empty;

        [JsonPropertyName("content_guid")]
        public string ContentGuid { get; set; } = string.Empty;

        [JsonPropertyName("english_content_version")]
        public string EnglishContentVersion { get; set; } = string.Empty;

        [JsonPropertyName("base_content_version")]
        public string BaseContentVersion { get; set; } = string.Empty;

        [JsonPropertyName("english_sha256")]
        public string EnglishSha256 { get; set; } = string.Empty;

        [JsonPropertyName("base_sha256")]
        public string BaseSha256 { get; set; } = string.Empty;

        [JsonPropertyName("stamp_sha256")]
        public string StampSha256 { get; set; } = string.Empty;

        [JsonPropertyName("stamp_value")]
        public string StampValue { get; set; } = string.Empty;

        [JsonPropertyName("english_locale_id")]
        public uint EnglishLocaleId { get; set; }

        [JsonPropertyName("english_locale_revision")]
        public uint EnglishLocaleRevision { get; set; }

        [JsonPropertyName("english_release_revision")]
        public uint EnglishReleaseRevision { get; set; }

        [JsonPropertyName("base_locale_id")]
        public uint BaseLocaleId { get; set; }

        [JsonPropertyName("base_locale_revision")]
        public uint BaseLocaleRevision { get; set; }

        [JsonPropertyName("base_release_revision")]
        public uint BaseReleaseRevision { get; set; }

        [JsonPropertyName("entry_count")]
        public int EntryCount { get; set; }

        [JsonPropertyName("readiness")]
        public string Readiness { get; set; } = "blocked";

        [JsonPropertyName("certified")]
        public bool Certified { get; set; }

        [JsonPropertyName("blocked_reason")]
        public string? BlockedReason { get; set; }

        [JsonPropertyName("translation_catalog_sha256")]
        public string? TranslationCatalogSha256 { get; set; }

        [JsonPropertyName("expected_output_sha256")]
        public string? ExpectedOutputSha256 { get; set; }

        [JsonPropertyName("minimum_applied_translations")]
        public int MinimumAppliedTranslations { get; set; } = 1;

        [JsonPropertyName("expected_applied_translations")]
        public int ExpectedAppliedTranslations { get; set; }

        [JsonPropertyName("expected_english_fallbacks")]
        public int ExpectedEnglishFallbacks { get; set; } = -1;

        [JsonPropertyName("expected_base_fallbacks")]
        public int ExpectedBaseFallbacks { get; set; } = -1;

        [JsonPropertyName("expected_needs_review_fallbacks")]
        public int ExpectedNeedsReviewFallbacks { get; set; } = -1;

        [JsonPropertyName("translation_policy")]
        public string TranslationPolicy { get; set; } = "release-approved";

        [JsonPropertyName("superseded_artifacts")]
        public RuntimeCacheSupersededArtifact[] SupersededArtifacts { get; set; } = Array.Empty<RuntimeCacheSupersededArtifact>();

        public static RuntimeCacheCompatibility Parse(string json)
        {
            var options = new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false
            };
            RuntimeCacheCompatibility? profile = JsonSerializer.Deserialize<RuntimeCacheCompatibility>(json, options);
            if (profile == null) throw new InvalidDataException("Runtime-cache compatibility profile is empty.");
            profile.Validate();
            return profile;
        }

        public void Validate()
        {
            if (Schema != 1 || !IsSafeId(Id) || string.IsNullOrWhiteSpace(GameVersion)
                || string.IsNullOrWhiteSpace(ContentGuid) || string.IsNullOrWhiteSpace(EnglishContentVersion)
                || string.IsNullOrWhiteSpace(BaseContentVersion) || string.IsNullOrWhiteSpace(StampValue))
            {
                throw new InvalidDataException("Runtime-cache compatibility identity is incomplete or unsafe.");
            }

            // Locale ids are game constants rather than per-build data: 1 is the en_US source slot and 8 is
            // the uk_UA slot this project overwrites. Locale and release revisions change with every content
            // drop, so a profile pins them per build instead of the parser fixing them to one observed release.
            if (!string.Equals(StampValue, GameVersion, StringComparison.Ordinal)
                || EnglishLocaleId != 1 || BaseLocaleId != 8
                || EnglishLocaleRevision == 0 || BaseLocaleRevision == 0)
            {
                throw new InvalidDataException("Runtime-cache profile contradicts the fixed EN/UK cache identity.");
            }

            ValidateHash(EnglishSha256, nameof(EnglishSha256));
            ValidateHash(BaseSha256, nameof(BaseSha256));
            ValidateHash(StampSha256, nameof(StampSha256));
            if (EntryCount <= 0 || MinimumAppliedTranslations <= 0 || MinimumAppliedTranslations > EntryCount)
            {
                throw new InvalidDataException("Runtime-cache entry and minimum translation counts are invalid.");
            }

            if (Readiness != "blocked" && Readiness != "ready")
            {
                throw new InvalidDataException("Runtime-cache readiness must be blocked or ready.");
            }

            if (Certified && Readiness != "ready")
            {
                throw new InvalidDataException("A certified runtime-cache profile must be ready.");
            }

            if (TranslationPolicy != "release-approved"
                && TranslationPolicy != "supervised-safe-drafts"
                && TranslationPolicy != "community-preview-all-drafts")
            {
                throw new InvalidDataException("Runtime-cache translation_policy must be release-approved, supervised-safe-drafts, or community-preview-all-drafts.");
            }

            if (Certified && (string.IsNullOrWhiteSpace(TranslationCatalogSha256) || string.IsNullOrWhiteSpace(ExpectedOutputSha256)))
            {
                throw new InvalidDataException("A certified runtime-cache profile must pin its catalog and exact raw output.");
            }

            if (Certified && (ExpectedAppliedTranslations < MinimumAppliedTranslations || ExpectedAppliedTranslations > EntryCount))
            {
                throw new InvalidDataException("A certified runtime-cache profile must pin an exact valid applied translation count.");
            }

            if (ExpectedEnglishFallbacks < -1 || ExpectedBaseFallbacks < -1 || ExpectedNeedsReviewFallbacks < -1)
            {
                throw new InvalidDataException("Runtime-cache fallback pins must be non-negative or omitted.");
            }

            if (Certified && ExpectedEnglishFallbacks >= 0 && ExpectedBaseFallbacks >= 0
                && ExpectedAppliedTranslations + ExpectedEnglishFallbacks + ExpectedBaseFallbacks != EntryCount)
            {
                throw new InvalidDataException("Certified runtime-cache composition pins must add up to entry_count.");
            }

            if (!string.IsNullOrWhiteSpace(TranslationCatalogSha256)) ValidateHash(TranslationCatalogSha256, nameof(TranslationCatalogSha256));
            if (!string.IsNullOrWhiteSpace(ExpectedOutputSha256)) ValidateHash(ExpectedOutputSha256, nameof(ExpectedOutputSha256));

            if (SupersededArtifacts == null || SupersededArtifacts.Length > 16)
            {
                throw new InvalidDataException("Runtime-cache superseded artifact allowlist is null or too large.");
            }

            for (int index = 0; index < SupersededArtifacts.Length; index++)
            {
                RuntimeCacheSupersededArtifact artifact = SupersededArtifacts[index]
                    ?? throw new InvalidDataException("Runtime-cache superseded artifact is null.");
                artifact.Validate(EntryCount);
                if (Hashing.FixedEqualsHex(artifact.OutputSha256, BaseSha256)
                    || (!string.IsNullOrWhiteSpace(ExpectedOutputSha256)
                        && Hashing.FixedEqualsHex(artifact.OutputSha256, ExpectedOutputSha256))
                    || (!string.IsNullOrWhiteSpace(TranslationCatalogSha256)
                        && Hashing.FixedEqualsHex(artifact.TranslationCatalogSha256, TranslationCatalogSha256)))
                {
                    throw new InvalidDataException("Runtime-cache superseded artifact duplicates the official base or current artifact.");
                }

                for (int prior = 0; prior < index; prior++)
                {
                    RuntimeCacheSupersededArtifact other = SupersededArtifacts[prior];
                    if (Hashing.FixedEqualsHex(artifact.OutputSha256, other.OutputSha256)
                        || Hashing.FixedEqualsHex(artifact.TranslationCatalogSha256, other.TranslationCatalogSha256))
                    {
                        throw new InvalidDataException("Runtime-cache superseded artifact allowlist contains duplicate output or catalog pins.");
                    }
                }
            }
        }

        public static RuntimeCacheCompatibility OfficialObserved0601239()
        {
            return new RuntimeCacheCompatibility
            {
                Id = "runtime-cache-win64-0.60.1239",
                GameVersion = "0.60.1239",
                ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa",
                EnglishContentVersion = "Prod_0.60.0_57",
                BaseContentVersion = "Prod_0.60.0_58",
                EnglishSha256 = "ECC39461923BA6C8DDF339B5EBFE719220FC0B4EC19E5469C1FAE427CD0CA6EA",
                BaseSha256 = "0EF3FB568835E746FCE88094B5E7B48F13D7E7ABE182FFC47958671391DE5A46",
                StampSha256 = "81399804E3559CE1F83E8A4F00533BCE290579AB00298CB362AD0B6BC156D054",
                StampValue = "0.60.1239",
                EnglishLocaleId = 1,
                EnglishLocaleRevision = 0xF458F128,
                EnglishReleaseRevision = 57,
                BaseLocaleId = 8,
                BaseLocaleRevision = 0x1BCA1660,
                BaseReleaseRevision = 58,
                EntryCount = 41290,
                MinimumAppliedTranslations = 576,
                ExpectedAppliedTranslations = 576,
                TranslationPolicy = "supervised-safe-drafts",
                Readiness = "blocked",
                Certified = false,
                BlockedReason = "No write-enabled catalog/output pins are embedded in the development build."
            };
        }

        private static bool IsSafeId(string value)
        {
            return value.Length is > 0 and <= 128
                && value.All(character => (character >= 'A' && character <= 'Z')
                    || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '-' || character == '_' || character == '.');
        }

        private static void ValidateHash(string? value, string name)
        {
            if (value == null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"{name} must be a SHA-256 hexadecimal digest.");
            }
        }
    }

    public sealed class RuntimeCacheSupersededArtifact
    {
        [JsonPropertyName("output_sha256")]
        public string OutputSha256 { get; set; } = string.Empty;

        [JsonPropertyName("translation_catalog_sha256")]
        public string TranslationCatalogSha256 { get; set; } = string.Empty;

        [JsonPropertyName("applied_translations")]
        public int AppliedTranslations { get; set; }

        [JsonPropertyName("english_fallbacks")]
        public int EnglishFallbacks { get; set; }

        [JsonPropertyName("base_fallbacks")]
        public int BaseFallbacks { get; set; }

        [JsonPropertyName("needs_review_fallbacks")]
        public int NeedsReviewFallbacks { get; set; }

        internal void Validate(int entryCount)
        {
            ValidateHash(OutputSha256, nameof(OutputSha256));
            ValidateHash(TranslationCatalogSha256, nameof(TranslationCatalogSha256));
            if (AppliedTranslations <= 0 || EnglishFallbacks < 0 || BaseFallbacks < 0
                || NeedsReviewFallbacks < 0 || NeedsReviewFallbacks > EnglishFallbacks
                || AppliedTranslations + EnglishFallbacks + BaseFallbacks != entryCount)
            {
                throw new InvalidDataException("Runtime-cache superseded artifact composition pins are invalid.");
            }
        }

        private static void ValidateHash(string? value, string name)
        {
            if (value == null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"{name} must be a SHA-256 hexadecimal digest.");
            }
        }
    }

    public sealed class RuntimeCacheInspection
    {
        public InstallationStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public string CacheRoot { get; set; } = string.Empty;
        public string EnglishPath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public string StampPath { get; set; } = string.Empty;
        public string? EnglishSha256 { get; set; }
        public string? BaseSha256 { get; set; }
        public string? StampSha256 { get; set; }
        public string? StampValue { get; set; }
        public string? EnglishContentVersion { get; set; }
        public string? BaseContentVersion { get; set; }
        public RuntimeCacheCompatibility Profile { get; set; } = new RuntimeCacheCompatibility();
        public PatchState? State { get; set; }
        public PatchJournal? Journal { get; set; }
    }
}
