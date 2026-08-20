using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace InvokersRu.Core.Updates
{
    /// <summary>
    /// Immutable, detached representation of a verified envelope. The deserialization DTO is deliberately
    /// not exposed after verification so future callers cannot alter data across the trust boundary.
    /// </summary>
    public sealed class VerifiedSignedUpdateEnvelope
    {
        internal VerifiedSignedUpdateEnvelope(SignedUpdateEnvelope source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Schema = source.Schema;
            Kind = source.Kind;
            KeyId = source.KeyId;
            Algorithm = source.Algorithm;
            PayloadBase64 = source.PayloadBase64;
            SignatureBase64 = source.SignatureBase64;
        }

        public int Schema { get; }
        public string Kind { get; }
        public string KeyId { get; }
        public string Algorithm { get; }
        public string PayloadBase64 { get; }
        public string SignatureBase64 { get; }
    }

    public sealed class VerifiedSignedUpdateManifest
    {
        internal VerifiedSignedUpdateManifest(SignedUpdateManifest source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Schema = source.Schema;
            Kind = source.Kind;
            Channel = source.Channel;
            Sequence = source.Sequence;
            ReleaseId = source.ReleaseId;
            IssuedUtc = source.IssuedUtc;
            ExpiresUtc = source.ExpiresUtc;
            Patcher = new VerifiedSignedUpdatePatcher(source.Patcher);
            Catalog = new VerifiedSignedUpdateCatalog(source.Catalog);

            var compatibility = new List<VerifiedSignedUpdateCompatibilityProfile>(source.Compatibility.Length);
            foreach (SignedUpdateCompatibilityProfile profile in source.Compatibility)
            {
                compatibility.Add(new VerifiedSignedUpdateCompatibilityProfile(profile));
            }

            Compatibility = new ReadOnlyCollection<VerifiedSignedUpdateCompatibilityProfile>(compatibility);
            RevokedReleaseIds = new ReadOnlyCollection<string>(new List<string>(source.RevokedReleaseIds));
            NotesRu = source.NotesRu;
        }

        public int Schema { get; }
        public string Kind { get; }
        public string Channel { get; }
        public ulong Sequence { get; }
        public string ReleaseId { get; }
        public string IssuedUtc { get; }
        public string ExpiresUtc { get; }
        public VerifiedSignedUpdatePatcher Patcher { get; }
        public VerifiedSignedUpdateCatalog Catalog { get; }
        public IReadOnlyList<VerifiedSignedUpdateCompatibilityProfile> Compatibility { get; }
        public IReadOnlyList<string> RevokedReleaseIds { get; }
        public string NotesRu { get; }
    }

    public sealed class VerifiedSignedUpdatePatcher
    {
        internal VerifiedSignedUpdatePatcher(SignedUpdatePatcher source)
        {
            ArgumentNullException.ThrowIfNull(source);
            MinimumVersion = source.MinimumVersion;
            LatestVersion = source.LatestVersion;
            DownloadPage = source.DownloadPage;
        }

        public string MinimumVersion { get; }
        public string LatestVersion { get; }
        public string DownloadPage { get; }
    }

    public sealed class VerifiedSignedUpdateCatalog
    {
        internal VerifiedSignedUpdateCatalog(SignedUpdateCatalog source)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArtifactId = source.ArtifactId;
            Url = source.Url;
            Compression = source.Compression;
            CompressedBytes = source.CompressedBytes;
            CompressedSha256 = source.CompressedSha256;
            UncompressedBytes = source.UncompressedBytes;
            UncompressedSha256 = source.UncompressedSha256;
            RecordCount = source.RecordCount;
            Format = source.Format;
            TranslationPolicy = source.TranslationPolicy;
        }

        public string ArtifactId { get; }
        public string Url { get; }
        public string Compression { get; }
        public long CompressedBytes { get; }
        public string CompressedSha256 { get; }
        public long UncompressedBytes { get; }
        public string UncompressedSha256 { get; }
        public int RecordCount { get; }
        public string Format { get; }
        public string TranslationPolicy { get; }
    }

    public sealed class VerifiedSignedUpdateCompatibilityProfile
    {
        internal VerifiedSignedUpdateCompatibilityProfile(SignedUpdateCompatibilityProfile source)
        {
            ArgumentNullException.ThrowIfNull(source);
            ProfileId = source.ProfileId;
            Mode = source.Mode;
            GameVersion = source.GameVersion;
            StampSha256 = source.StampSha256;
            StampValue = source.StampValue;
            ContentGuid = source.ContentGuid;
            Loc1Schema = source.Loc1Schema;
            OrderedKeysetSha256 = source.OrderedKeysetSha256;
            English = new VerifiedSignedUpdateCorpusIdentity(source.English);
            Base = new VerifiedSignedUpdateCorpusIdentity(source.Base);
            Composition = new VerifiedSignedUpdateComposition(source.Composition);
        }

        public string ProfileId { get; }
        public string Mode { get; }
        public string GameVersion { get; }
        public string StampSha256 { get; }
        public string StampValue { get; }
        public string ContentGuid { get; }
        public int Loc1Schema { get; }
        public string OrderedKeysetSha256 { get; }
        public VerifiedSignedUpdateCorpusIdentity English { get; }
        public VerifiedSignedUpdateCorpusIdentity Base { get; }
        public VerifiedSignedUpdateComposition Composition { get; }
    }

    public sealed class VerifiedSignedUpdateCorpusIdentity
    {
        internal VerifiedSignedUpdateCorpusIdentity(SignedUpdateCorpusIdentity source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Sha256 = source.Sha256;
            ContentVersion = source.ContentVersion;
            LocaleId = source.LocaleId;
            LocaleRevisionHex = source.LocaleRevisionHex;
            ReleaseRevision = source.ReleaseRevision;
            EntryCount = source.EntryCount;
        }

        public string Sha256 { get; }
        public string ContentVersion { get; }
        public uint LocaleId { get; }
        public string LocaleRevisionHex { get; }
        public uint ReleaseRevision { get; }
        public int EntryCount { get; }
    }

    public sealed class VerifiedSignedUpdateComposition
    {
        internal VerifiedSignedUpdateComposition(SignedUpdateComposition source)
        {
            ArgumentNullException.ThrowIfNull(source);
            AppliedRu = source.AppliedRu;
            EnglishFallback = source.EnglishFallback;
            BaseFallback = source.BaseFallback;
            MissingCatalog = source.MissingCatalog;
            StaleCatalog = source.StaleCatalog;
            RejectedCatalog = source.RejectedCatalog;
            NeedsReviewFallback = source.NeedsReviewFallback;
            PolicyFallback = source.PolicyFallback;
            ValidationErrors = source.ValidationErrors;
            ValidationWarnings = source.ValidationWarnings;
            OutputRawSha256 = source.OutputRawSha256;
        }

        public int AppliedRu { get; }
        public int EnglishFallback { get; }
        public int BaseFallback { get; }
        public int MissingCatalog { get; }
        public int StaleCatalog { get; }
        public int RejectedCatalog { get; }
        public int NeedsReviewFallback { get; }
        public int PolicyFallback { get; }
        public int ValidationErrors { get; }
        public int ValidationWarnings { get; }
        public string OutputRawSha256 { get; }
    }
}
