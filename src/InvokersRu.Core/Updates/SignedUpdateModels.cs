using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Updates
{
    /// <summary>
    /// Fixed protocol limits. They are deliberately part of the verifier rather than supplied by an
    /// untrusted manifest.
    /// </summary>
    public static class SignedUpdateLimits
    {
        public const int MaxEnvelopeBytes = 384 * 1024;
        public const int MaxPayloadBytes = 256 * 1024;
        public const int MaxPublicKeyBytes = 512;
        public const long MaxCompressedCatalogBytes = 64L * 1024 * 1024;
        public const long MaxUncompressedCatalogBytes = 128L * 1024 * 1024;
        public const int MaxCatalogRecords = 100_000;
        public const int MaxValidationWarnings = 1_000_000;
        public const int MaxCompatibilityProfiles = 64;
        public const int MaxRevokedReleaseIds = 1_024;
        public const int MaxNotesCharacters = 4_096;
        public const int MaxUrlCharacters = 2_048;
        public const int MaxCompressionRatio = 200;
        public static readonly TimeSpan MaximumManifestLifetime = TimeSpan.FromDays(45);
        public static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(15);
    }

    public sealed class SignedUpdateEnvelope
    {
        [JsonRequired]
        [JsonPropertyName("schema")]
        public int Schema { get; init; }

        [JsonRequired]
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("key_id")]
        public string KeyId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("algorithm")]
        public string Algorithm { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("payload_b64")]
        public string PayloadBase64 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("signature_b64")]
        public string SignatureBase64 { get; init; } = string.Empty;
    }

    public sealed class SignedUpdateManifest
    {
        [JsonRequired]
        [JsonPropertyName("schema")]
        public int Schema { get; init; }

        [JsonRequired]
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("channel")]
        public string Channel { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("sequence")]
        public ulong Sequence { get; init; }

        [JsonRequired]
        [JsonPropertyName("release_id")]
        public string ReleaseId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("issued_utc")]
        public string IssuedUtc { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("expires_utc")]
        public string ExpiresUtc { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("patcher")]
        public SignedUpdatePatcher Patcher { get; init; } = new SignedUpdatePatcher();

        [JsonRequired]
        [JsonPropertyName("catalog")]
        public SignedUpdateCatalog Catalog { get; init; } = new SignedUpdateCatalog();

        [JsonRequired]
        [JsonPropertyName("compatibility")]
        public SignedUpdateCompatibilityProfile[] Compatibility { get; init; } = Array.Empty<SignedUpdateCompatibilityProfile>();

        [JsonRequired]
        [JsonPropertyName("revoked_release_ids")]
        public string[] RevokedReleaseIds { get; init; } = Array.Empty<string>();

        [JsonRequired]
        [JsonPropertyName("notes_ru")]
        public string NotesRu { get; init; } = string.Empty;
    }

    public sealed class SignedUpdatePatcher
    {
        [JsonRequired]
        [JsonPropertyName("minimum_version")]
        public string MinimumVersion { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("latest_version")]
        public string LatestVersion { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("download_page")]
        public string DownloadPage { get; init; } = string.Empty;
    }

    public sealed class SignedUpdateCatalog
    {
        [JsonRequired]
        [JsonPropertyName("artifact_id")]
        public string ArtifactId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("compression")]
        public string Compression { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("compressed_bytes")]
        public long CompressedBytes { get; init; }

        [JsonRequired]
        [JsonPropertyName("compressed_sha256")]
        public string CompressedSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("uncompressed_bytes")]
        public long UncompressedBytes { get; init; }

        [JsonRequired]
        [JsonPropertyName("uncompressed_sha256")]
        public string UncompressedSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("record_count")]
        public int RecordCount { get; init; }

        [JsonRequired]
        [JsonPropertyName("format")]
        public string Format { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("translation_policy")]
        public string TranslationPolicy { get; init; } = string.Empty;
    }

    public sealed class SignedUpdateCompatibilityProfile
    {
        [JsonRequired]
        [JsonPropertyName("profile_id")]
        public string ProfileId { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("mode")]
        public string Mode { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("game_version")]
        public string GameVersion { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("stamp_sha256")]
        public string StampSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("stamp_value")]
        public string StampValue { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("content_guid")]
        public string ContentGuid { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("loc1_schema")]
        public int Loc1Schema { get; init; }

        [JsonRequired]
        [JsonPropertyName("ordered_keyset_sha256")]
        public string OrderedKeysetSha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("english")]
        public SignedUpdateCorpusIdentity English { get; init; } = new SignedUpdateCorpusIdentity();

        [JsonRequired]
        [JsonPropertyName("base")]
        public SignedUpdateCorpusIdentity Base { get; init; } = new SignedUpdateCorpusIdentity();

        [JsonRequired]
        [JsonPropertyName("composition")]
        public SignedUpdateComposition Composition { get; init; } = new SignedUpdateComposition();
    }

    public sealed class SignedUpdateCorpusIdentity
    {
        [JsonRequired]
        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("content_version")]
        public string ContentVersion { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("locale_id")]
        public uint LocaleId { get; init; }

        [JsonRequired]
        [JsonPropertyName("locale_revision_hex")]
        public string LocaleRevisionHex { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("release_revision")]
        public uint ReleaseRevision { get; init; }

        [JsonRequired]
        [JsonPropertyName("entry_count")]
        public int EntryCount { get; init; }
    }

    public sealed class SignedUpdateComposition
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

        [JsonRequired]
        [JsonPropertyName("validation_errors")]
        public int ValidationErrors { get; init; }

        [JsonRequired]
        [JsonPropertyName("validation_warnings")]
        public int ValidationWarnings { get; init; }

        [JsonRequired]
        [JsonPropertyName("output_raw_sha256")]
        public string OutputRawSha256 { get; init; } = string.Empty;
    }

    public enum SignedUpdatePatcherDisposition
    {
        Current,
        UpdateAvailable,
        TooOld
    }

    public enum SignedUpdateWarningCode
    {
        SequenceAlreadyAccepted,
        ExpiredCachedManifest,
        PatcherUpdateAvailable,
        PatcherTooOld
    }

    public sealed class SignedUpdateVerificationContext
    {
        public SignedUpdateVerificationContext(
            DateTimeOffset nowUtc,
            string runningPatcherVersion,
            ulong highestAcceptedSequence = 0,
            string? highestAcceptedPayloadSha256 = null,
            bool allowExpiredCachedManifest = false)
        {
            NowUtc = nowUtc.ToUniversalTime();
            RunningPatcherVersion = runningPatcherVersion;
            HighestAcceptedSequence = highestAcceptedSequence;
            HighestAcceptedPayloadSha256 = highestAcceptedPayloadSha256;
            AllowExpiredCachedManifest = allowExpiredCachedManifest;
        }

        public DateTimeOffset NowUtc { get; }
        public string RunningPatcherVersion { get; }
        public ulong HighestAcceptedSequence { get; }
        public string? HighestAcceptedPayloadSha256 { get; }
        public bool AllowExpiredCachedManifest { get; }
    }

    public sealed class VerifiedSignedUpdate
    {
        internal VerifiedSignedUpdate(
            SignedUpdateEnvelope envelope,
            SignedUpdateManifest manifest,
            string payloadSha256,
            DateTimeOffset issuedUtc,
            DateTimeOffset expiresUtc,
            bool isExpired,
            bool isNewSequence,
            SignedUpdatePatcherDisposition patcherDisposition,
            IReadOnlyList<SignedUpdateWarningCode> warnings)
        {
            Envelope = new VerifiedSignedUpdateEnvelope(envelope);
            Manifest = new VerifiedSignedUpdateManifest(manifest);
            PayloadSha256 = payloadSha256;
            IssuedUtc = issuedUtc;
            ExpiresUtc = expiresUtc;
            IsExpired = isExpired;
            IsNewSequence = isNewSequence;
            PatcherDisposition = patcherDisposition;
            Warnings = new List<SignedUpdateWarningCode>(warnings).AsReadOnly();
        }

        public VerifiedSignedUpdateEnvelope Envelope { get; }
        public VerifiedSignedUpdateManifest Manifest { get; }
        public string PayloadSha256 { get; }
        public DateTimeOffset IssuedUtc { get; }
        public DateTimeOffset ExpiresUtc { get; }
        public bool IsExpired { get; }
        public bool IsNewSequence { get; }
        public SignedUpdatePatcherDisposition PatcherDisposition { get; }
        public IReadOnlyList<SignedUpdateWarningCode> Warnings { get; }

        /// <summary>
        /// Expired metadata may authenticate a last-known-good offline artifact, but it must not authorize a
        /// network download. A patcher older than minimum_version is blocked for the same reason.
        /// </summary>
        public bool CanDownloadRemoteArtifact => CanDownloadRemoteArtifactAt(DateTimeOffset.UtcNow);

        public bool IsExpiredAt(DateTimeOffset nowUtc)
        {
            return nowUtc.ToUniversalTime() > ExpiresUtc;
        }

        public bool CanDownloadRemoteArtifactAt(DateTimeOffset nowUtc)
        {
            return !IsExpiredAt(nowUtc) && PatcherDisposition != SignedUpdatePatcherDisposition.TooOld;
        }
    }
}
