using System;
using System.Text.Json.Serialization;

namespace InvokersRu.UpdateReleaseTool
{
    internal sealed class PublicKeyConfiguration
    {
        [JsonRequired]
        [JsonPropertyName("schema")]
        public int Schema { get; init; } = 1;

        [JsonRequired]
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "invokers-ru-update-public-key";

        [JsonRequired]
        [JsonPropertyName("key_id")]
        public string KeyId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("algorithm")]
        public string Algorithm { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("subject_public_key_info_b64")]
        public string SubjectPublicKeyInfoBase64 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("subject_public_key_info_sha256")]
        public string SubjectPublicKeyInfoSha256 { get; init; } = string.Empty;
    }

    internal sealed class SigningSequenceState
    {
        [JsonRequired]
        [JsonPropertyName("schema")]
        public int Schema { get; init; } = 1;

        [JsonRequired]
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "invokers-ru-update-signing-state";

        [JsonRequired]
        [JsonPropertyName("key_id")]
        public string KeyId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("highest_reserved_sequence")]
        public ulong HighestReservedSequence { get; set; }

        [JsonRequired]
        [JsonPropertyName("records")]
        public SigningSequenceRecord[] Records { get; set; } = Array.Empty<SigningSequenceRecord>();
    }

    internal sealed class SigningSequenceRecord
    {
        [JsonRequired]
        [JsonPropertyName("sequence")]
        public ulong Sequence { get; init; }

        [JsonRequired]
        [JsonPropertyName("release_id")]
        public string ReleaseId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("payload_sha256")]
        public string PayloadSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("reserved_utc")]
        public string ReservedUtc { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("status")]
        public string Status { get; set; } = "reserved";

        [JsonPropertyName("completed_utc")]
        public string? CompletedUtc { get; set; }
    }

    internal sealed class ReleaseReceipt
    {
        [JsonPropertyName("schema")]
        public int Schema { get; init; } = 1;

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "invokers-ru-update-release-receipt";

        [JsonPropertyName("release_id")]
        public string ReleaseId { get; init; } = string.Empty;

        [JsonPropertyName("sequence")]
        public ulong Sequence { get; init; }

        [JsonPropertyName("key_id")]
        public string KeyId { get; init; } = string.Empty;

        [JsonPropertyName("catalog_file")]
        public string CatalogFile { get; init; } = string.Empty;

        [JsonPropertyName("catalog_record_count")]
        public int CatalogRecordCount { get; init; }

        [JsonPropertyName("catalog_uncompressed_bytes")]
        public long CatalogUncompressedBytes { get; init; }

        [JsonPropertyName("catalog_uncompressed_sha256")]
        public string CatalogUncompressedSha256 { get; init; } = string.Empty;

        [JsonPropertyName("catalog_compressed_bytes")]
        public long CatalogCompressedBytes { get; init; }

        [JsonPropertyName("catalog_compressed_sha256")]
        public string CatalogCompressedSha256 { get; init; } = string.Empty;

        [JsonPropertyName("payload_file")]
        public string PayloadFile { get; init; } = string.Empty;

        [JsonPropertyName("payload_sha256")]
        public string PayloadSha256 { get; init; } = string.Empty;

        [JsonPropertyName("envelope_file")]
        public string EnvelopeFile { get; init; } = string.Empty;

        [JsonPropertyName("envelope_sha256")]
        public string EnvelopeSha256 { get; init; } = string.Empty;

        [JsonPropertyName("compatibility_profile_count")]
        public int CompatibilityProfileCount { get; init; }

        [JsonPropertyName("verified_by_core")]
        public bool VerifiedByCore { get; init; }
    }

    internal sealed class BuildReleaseRequest
    {
        public string RepositoryRoot { get; init; } = string.Empty;
        public string CatalogPath { get; init; } = string.Empty;
        public string CompatibilityPath { get; init; } = string.Empty;
        public string PrivateKeyPath { get; init; } = string.Empty;
        public string SigningStatePath { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public string ReleaseId { get; init; } = string.Empty;
        public string ArtifactId { get; init; } = string.Empty;
        public string IssuedUtc { get; init; } = string.Empty;
        public string ExpiresUtc { get; init; } = string.Empty;
        public string MinimumPatcherVersion { get; init; } = string.Empty;
        public string LatestPatcherVersion { get; init; } = string.Empty;
        public string TranslationPolicy { get; init; } = string.Empty;
        public string NotesRu { get; init; } = string.Empty;
        public string? RevokedReleaseIdsPath { get; init; }
        public ulong Sequence { get; init; }
        public ulong ExpectedPreviousSequence { get; init; }
    }

    internal sealed class BuildCompatibilityRequest
    {
        public string RuntimeProfilePath { get; init; } = string.Empty;
        public string EnglishLoc1Path { get; init; } = string.Empty;
        public string BaseLoc1Path { get; init; } = string.Empty;
        public string StampPath { get; init; } = string.Empty;
        public string CatalogPath { get; init; } = string.Empty;
        public string BuiltLoc1Path { get; init; } = string.Empty;
        public string BuildReportPath { get; init; } = string.Empty;
        public string OutputPath { get; init; } = string.Empty;
    }

    internal sealed class CliBuildReport
    {
        [JsonRequired]
        [JsonPropertyName("schema")]
        public int Schema { get; init; }

        [JsonRequired]
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("source")]
        public CliBuildReportSource Source { get; init; } = new CliBuildReportSource();

        [JsonRequired]
        [JsonPropertyName("target")]
        public CliBuildReportTarget Target { get; init; } = new CliBuildReportTarget();

        [JsonRequired]
        [JsonPropertyName("composition")]
        public CliBuildReportComposition Composition { get; init; } = new CliBuildReportComposition();

        [JsonRequired]
        [JsonPropertyName("validation")]
        public CliBuildReportValidation Validation { get; init; } = new CliBuildReportValidation();

        [JsonRequired]
        [JsonPropertyName("build_options")]
        public CliBuildReportOptions BuildOptions { get; init; } = new CliBuildReportOptions();

        [JsonRequired]
        [JsonPropertyName("output")]
        public CliBuildReportOutput Output { get; init; } = new CliBuildReportOutput();
    }

    internal sealed class CliBuildReportSource
    {
        [JsonRequired]
        [JsonPropertyName("english_content_guid")]
        public string EnglishContentGuid { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("english_content_version")]
        public string EnglishContentVersion { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("english_schema")]
        public uint EnglishSchema { get; init; }

        [JsonRequired]
        [JsonPropertyName("english_container_sha256")]
        public string EnglishContainerSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("english_raw_sha256")]
        public string EnglishRawSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("base_content_guid")]
        public string BaseContentGuid { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("base_content_version")]
        public string BaseContentVersion { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("base_schema")]
        public uint BaseSchema { get; init; }

        [JsonRequired]
        [JsonPropertyName("base_container_sha256")]
        public string BaseContainerSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("base_raw_sha256")]
        public string BaseRawSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("translations_sha256")]
        public string TranslationsSha256 { get; init; } = string.Empty;
    }

    internal sealed class CliBuildReportTarget
    {
        [JsonRequired]
        [JsonPropertyName("locale_id")]
        public uint LocaleId { get; init; }

        [JsonRequired]
        [JsonPropertyName("locale_revision")]
        public string LocaleRevision { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("release_revision")]
        public uint ReleaseRevision { get; init; }

        [JsonRequired]
        [JsonPropertyName("entries")]
        public int Entries { get; init; }
    }

    internal sealed class CliBuildReportComposition
    {
        [JsonRequired]
        [JsonPropertyName("applied_ru")]
        public int AppliedRu { get; init; }

        [JsonRequired]
        [JsonPropertyName("english_fallback")]
        public int EnglishFallback { get; init; }

        [JsonRequired]
        [JsonPropertyName("base_fallback")]
        public int BaseFallback { get; init; }

        [JsonRequired]
        [JsonPropertyName("missing_catalog")]
        public int MissingCatalog { get; init; }

        [JsonRequired]
        [JsonPropertyName("stale_catalog")]
        public int StaleCatalog { get; init; }

        [JsonRequired]
        [JsonPropertyName("rejected_catalog")]
        public int RejectedCatalog { get; init; }

        [JsonRequired]
        [JsonPropertyName("needs_review_fallback")]
        public int NeedsReviewFallback { get; init; }

        [JsonRequired]
        [JsonPropertyName("policy_fallback")]
        public int PolicyFallback { get; init; }
    }

    internal sealed class CliBuildReportValidation
    {
        [JsonRequired]
        [JsonPropertyName("profile")]
        public string Profile { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("errors")]
        public int Errors { get; init; }

        [JsonRequired]
        [JsonPropertyName("warnings")]
        public int Warnings { get; init; }
    }

    internal sealed class CliBuildReportOptions
    {
        [JsonRequired]
        [JsonPropertyName("include_draft")]
        public bool IncludeDraft { get; init; }

        [JsonRequired]
        [JsonPropertyName("exclude_needs_review")]
        public bool ExcludeNeedsReview { get; init; }

        [JsonRequired]
        [JsonPropertyName("per_locale_content_version")]
        public bool PerLocaleContentVersion { get; init; }

        [JsonRequired]
        [JsonPropertyName("container")]
        public string Container { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("release")]
        public bool Release { get; init; }
    }

    internal sealed class CliBuildReportOutput
    {
        [JsonRequired]
        [JsonPropertyName("bytes")]
        public long Bytes { get; init; }

        [JsonRequired]
        [JsonPropertyName("raw_bytes")]
        public long RawBytes { get; init; }

        [JsonRequired]
        [JsonPropertyName("container_sha256")]
        public string ContainerSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("raw_sha256")]
        public string RawSha256 { get; init; } = string.Empty;
    }
}
