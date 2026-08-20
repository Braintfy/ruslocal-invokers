using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Updates
{
    public static class SignedUpdateVerifier
    {
        public const string EnvelopeKind = "invokers-ru-signed-update-envelope";
        public const string ManifestKind = "invokers-ru-update-manifest";
        public const string SignatureAlgorithm = "ecdsa-p256-sha256-p1363";
        private const string NistP256Oid = "1.2.840.10045.3.1.7";

        private static readonly JsonSerializerOptions StrictJson = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            MaxDepth = 32,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        /// <summary>
        /// Verifies an envelope signed over the exact decoded payload bytes and then strictly parses the
        /// payload. publicKeySubjectPublicKeyInfo must contain only one DER SubjectPublicKeyInfo P-256 key,
        /// and expectedKeyId must be the id pinned alongside that key. The caller owns anti-rollback
        /// persistence; this method validates the supplied persisted state.
        /// </summary>
        public static VerifiedSignedUpdate Verify(
            ReadOnlySpan<byte> envelopeUtf8,
            ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo,
            string expectedKeyId,
            SignedUpdateVerificationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            RejectInvalidUtf8Container(envelopeUtf8, SignedUpdateLimits.MaxEnvelopeBytes, "Signed update envelope");
            if (publicKeySubjectPublicKeyInfo.Length is < 1 or > SignedUpdateLimits.MaxPublicKeyBytes)
            {
                throw new InvalidDataException("Update public key size is invalid.");
            }

            SignedUpdateEnvelope envelope = DeserializeStrict<SignedUpdateEnvelope>(envelopeUtf8, "Signed update envelope");
            ValidateEnvelope(envelope, expectedKeyId);
            byte[] payload = DecodeCanonicalBase64(envelope.PayloadBase64, SignedUpdateLimits.MaxPayloadBytes, "payload_b64");
            byte[] signature = DecodeCanonicalBase64(envelope.SignatureBase64, 64, "signature_b64");
            if (signature.Length != 64)
            {
                throw new InvalidDataException("P-256 IEEE-P1363 signature must be exactly 64 bytes.");
            }

            VerifySignature(payload, signature, publicKeySubjectPublicKeyInfo);
            string payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
            RejectInvalidUtf8Container(payload, SignedUpdateLimits.MaxPayloadBytes, "Signed update payload");
            SignedUpdateManifest manifest = DeserializeStrict<SignedUpdateManifest>(payload, "Signed update payload");
            ValidationFacts facts = ValidateManifest(manifest, context, payloadSha256);

            var warnings = new List<SignedUpdateWarningCode>();
            bool isNewSequence = manifest.Sequence > context.HighestAcceptedSequence;
            if (!isNewSequence) warnings.Add(SignedUpdateWarningCode.SequenceAlreadyAccepted);
            if (facts.IsExpired) warnings.Add(SignedUpdateWarningCode.ExpiredCachedManifest);
            if (facts.PatcherDisposition == SignedUpdatePatcherDisposition.UpdateAvailable)
            {
                warnings.Add(SignedUpdateWarningCode.PatcherUpdateAvailable);
            }
            else if (facts.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld)
            {
                warnings.Add(SignedUpdateWarningCode.PatcherTooOld);
            }

            return new VerifiedSignedUpdate(
                envelope,
                manifest,
                payloadSha256,
                facts.IssuedUtc,
                facts.ExpiresUtc,
                facts.IsExpired,
                isNewSequence,
                facts.PatcherDisposition,
                warnings.AsReadOnly());
        }

        private static T DeserializeStrict<T>(ReadOnlySpan<byte> utf8, string label) where T : class
        {
            try
            {
                T? result = JsonSerializer.Deserialize<T>(utf8, StrictJson);
                return result ?? throw new InvalidDataException($"{label} is JSON null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{label} is not strict schema-valid JSON: {exception.Message}", exception);
            }
        }

        private static void ValidateEnvelope(SignedUpdateEnvelope envelope, string expectedKeyId)
        {
            if (envelope.Schema != 1
                || !string.Equals(envelope.Kind, EnvelopeKind, StringComparison.Ordinal)
                || !string.Equals(envelope.Algorithm, SignatureAlgorithm, StringComparison.Ordinal)
                || !IsSafeId(envelope.KeyId)
                || !IsSafeId(expectedKeyId)
                || !string.Equals(envelope.KeyId, expectedKeyId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed update envelope identity, key id, or algorithm is unsupported.");
            }
        }

        private static void VerifySignature(
            ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> subjectPublicKeyInfo)
        {
            using ECDsa verifier = ECDsa.Create();
            try
            {
                verifier.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
                ECParameters parameters = verifier.ExportParameters(includePrivateParameters: false);
                if (bytesRead != subjectPublicKeyInfo.Length
                    || verifier.KeySize != 256
                    || !string.Equals(parameters.Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal))
                {
                    throw new CryptographicException("Update key must be exactly one NIST P-256 SubjectPublicKeyInfo key.");
                }
            }
            catch (CryptographicException exception)
            {
                throw new CryptographicException("Update public key is not a valid pinned NIST P-256 key.", exception);
            }

            if (!verifier.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new CryptographicException("Signed update payload signature is invalid.");
            }
        }

        private static ValidationFacts ValidateManifest(
            SignedUpdateManifest manifest,
            SignedUpdateVerificationContext context,
            string payloadSha256)
        {
            if (manifest.Schema != 1
                || !string.Equals(manifest.Kind, ManifestKind, StringComparison.Ordinal)
                || !string.Equals(manifest.Channel, "stable", StringComparison.Ordinal)
                || manifest.Sequence == 0
                || !IsSafeId(manifest.ReleaseId)
                || manifest.Patcher == null
                || manifest.Catalog == null)
            {
                throw new InvalidDataException("Signed update manifest identity, channel, sequence, or release id is invalid.");
            }

            ValidateSequence(manifest.Sequence, payloadSha256, context);
            DateTimeOffset issuedUtc = ParseUtcSecond(manifest.IssuedUtc, "issued_utc");
            DateTimeOffset expiresUtc = ParseUtcSecond(manifest.ExpiresUtc, "expires_utc");
            if (expiresUtc <= issuedUtc || expiresUtc - issuedUtc > SignedUpdateLimits.MaximumManifestLifetime)
            {
                throw new InvalidDataException("Signed update lifetime is empty or exceeds the fixed maximum.");
            }

            if (issuedUtc > context.NowUtc + SignedUpdateLimits.MaximumFutureClockSkew)
            {
                throw new InvalidDataException("Signed update was issued too far in the future.");
            }

            bool isExpired = context.NowUtc > expiresUtc;
            if (isExpired && !context.AllowExpiredCachedManifest)
            {
                throw new InvalidDataException("Signed update manifest is expired; it may only be considered as an explicit offline cache fallback.");
            }

            Version minimumVersion = ParsePatcherVersion(manifest.Patcher.MinimumVersion, "minimum_version");
            Version latestVersion = ParsePatcherVersion(manifest.Patcher.LatestVersion, "latest_version");
            Version runningVersion = ParsePatcherVersion(context.RunningPatcherVersion, nameof(context.RunningPatcherVersion));
            if (minimumVersion > latestVersion)
            {
                throw new InvalidDataException("Patcher minimum_version cannot exceed latest_version.");
            }

            SignedUpdateUrlPolicy.ValidatePatcherDownloadPage(manifest.Patcher.DownloadPage);
            ValidateCatalog(manifest.Catalog, manifest.ReleaseId);
            ValidateCompatibility(manifest.Compatibility, manifest.Catalog);
            ValidateRevocations(manifest.RevokedReleaseIds, manifest.ReleaseId);
            ValidateDisplayText(manifest.NotesRu, "notes_ru", SignedUpdateLimits.MaxNotesCharacters);

            SignedUpdatePatcherDisposition patcherDisposition = runningVersion < minimumVersion
                ? SignedUpdatePatcherDisposition.TooOld
                : runningVersion < latestVersion
                    ? SignedUpdatePatcherDisposition.UpdateAvailable
                    : SignedUpdatePatcherDisposition.Current;
            return new ValidationFacts(issuedUtc, expiresUtc, isExpired, patcherDisposition);
        }

        private static void ValidateSequence(
            ulong sequence,
            string payloadSha256,
            SignedUpdateVerificationContext context)
        {
            if (context.HighestAcceptedSequence == 0)
            {
                if (context.HighestAcceptedPayloadSha256 != null)
                {
                    throw new InvalidDataException("Anti-rollback state has a payload hash without a sequence.");
                }

                return;
            }

            ValidateUpperHex(context.HighestAcceptedPayloadSha256, 64, "highest accepted payload SHA-256");
            if (sequence < context.HighestAcceptedSequence)
            {
                throw new InvalidDataException("Signed update sequence is older than the highest accepted sequence.");
            }

            if (sequence == context.HighestAcceptedSequence
                && !FixedEqualsUpperHex(payloadSha256, context.HighestAcceptedPayloadSha256!))
            {
                throw new InvalidDataException("Signed update reused an accepted sequence with different payload bytes.");
            }
        }

        private static void ValidateCatalog(SignedUpdateCatalog catalog, string releaseId)
        {
            if (!IsSafeId(catalog.ArtifactId)
                || (catalog.Compression != "brotli" && catalog.Compression != "none")
                || catalog.Format != "invokers-ru-jsonl-v1"
                || (catalog.TranslationPolicy != "release-approved-v1"
                    && catalog.TranslationPolicy != "validated-preview-v1"))
            {
                throw new InvalidDataException("Catalog artifact identity, compression, format, or translation policy is unsupported.");
            }

            ValidateUpperHex(catalog.CompressedSha256, 64, "compressed_sha256");
            ValidateUpperHex(catalog.UncompressedSha256, 64, "uncompressed_sha256");
            if (catalog.CompressedBytes is < 1 or > SignedUpdateLimits.MaxCompressedCatalogBytes
                || catalog.UncompressedBytes is < 1 or > SignedUpdateLimits.MaxUncompressedCatalogBytes
                || catalog.RecordCount is < 1 or > SignedUpdateLimits.MaxCatalogRecords
                || catalog.UncompressedBytes > catalog.CompressedBytes * SignedUpdateLimits.MaxCompressionRatio)
            {
                throw new InvalidDataException("Catalog byte counts, record count, or compression ratio exceed fixed caps.");
            }

            if (catalog.Compression == "none"
                && (catalog.CompressedBytes != catalog.UncompressedBytes
                    || !FixedEqualsUpperHex(catalog.CompressedSha256, catalog.UncompressedSha256)))
            {
                throw new InvalidDataException("An uncompressed catalog must have identical transport and content pins.");
            }

            SignedUpdateUrlPolicy.ValidateCatalogUrl(catalog.Url, releaseId);
        }

        private static void ValidateCompatibility(
            SignedUpdateCompatibilityProfile[]? profiles,
            SignedUpdateCatalog catalog)
        {
            if (profiles == null
                || profiles.Length is < 1 or > SignedUpdateLimits.MaxCompatibilityProfiles
                || profiles.Any(profile => profile == null)
                || profiles.GroupBy(profile => profile.ProfileId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("Compatibility must contain a bounded unique set of exact profiles.");
            }

            foreach (SignedUpdateCompatibilityProfile profile in profiles)
            {
                if (profile == null) throw new InvalidDataException("Compatibility profile cannot be null.");
                ValidateCompatibilityProfile(profile, catalog);
            }
        }

        private static void ValidateCompatibilityProfile(
            SignedUpdateCompatibilityProfile profile,
            SignedUpdateCatalog catalog)
        {
            if (!IsSafeId(profile.ProfileId)
                || profile.Mode != "exact"
                || !IsSafeVersionToken(profile.GameVersion)
                || !string.Equals(profile.GameVersion, profile.StampValue, StringComparison.Ordinal)
                || !Guid.TryParseExact(profile.ContentGuid, "D", out Guid contentGuid)
                || !string.Equals(contentGuid.ToString("D"), profile.ContentGuid, StringComparison.Ordinal)
                || profile.Loc1Schema is < 1 or > 64)
            {
                throw new InvalidDataException("Compatibility profile identity is incomplete, non-canonical, or not exact.");
            }

            ValidateUpperHex(profile.StampSha256, 64, "stamp_sha256");
            ValidateUpperHex(profile.OrderedKeysetSha256, 64, "ordered_keyset_sha256");
            ValidateCorpusIdentity(profile.English, expectedLocaleId: 1, "english");
            ValidateCorpusIdentity(profile.Base, expectedLocaleId: 8, "base");
            if (profile.English.EntryCount != profile.Base.EntryCount)
            {
                throw new InvalidDataException("English and base corpus entry counts differ.");
            }

            ValidateComposition(profile.Composition, profile.Base.EntryCount, catalog.RecordCount);
        }

        private static void ValidateCorpusIdentity(
            SignedUpdateCorpusIdentity? identity,
            uint expectedLocaleId,
            string name)
        {
            if (identity == null
                || identity.LocaleId != expectedLocaleId
                || identity.ReleaseRevision == 0
                || identity.EntryCount is < 1 or > SignedUpdateLimits.MaxCatalogRecords
                || !IsSafeContentVersion(identity.ContentVersion))
            {
                throw new InvalidDataException($"{name} corpus identity is incomplete or invalid.");
            }

            ValidateUpperHex(identity.Sha256, 64, $"{name}.sha256");
            ValidateUpperHex(identity.LocaleRevisionHex, 8, $"{name}.locale_revision_hex");
            if (identity.LocaleRevisionHex == "00000000")
            {
                throw new InvalidDataException($"{name} locale revision cannot be zero.");
            }
        }

        private static void ValidateComposition(
            SignedUpdateComposition? composition,
            int entryCount,
            int catalogRecordCount)
        {
            if (composition == null)
            {
                throw new InvalidDataException("Exact compatibility composition is missing.");
            }

            int[] counts =
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
            if (counts.Any(count => count < 0 || count > entryCount)
                || composition.ValidationWarnings is < 0 or > SignedUpdateLimits.MaxValidationWarnings
                || composition.AppliedRu > catalogRecordCount
                || (long)composition.AppliedRu + composition.EnglishFallback + composition.BaseFallback != entryCount
                || (long)composition.MissingCatalog + composition.StaleCatalog + composition.RejectedCatalog > composition.EnglishFallback
                || (long)composition.NeedsReviewFallback + composition.PolicyFallback > composition.EnglishFallback
                || composition.ValidationErrors != 0)
            {
                throw new InvalidDataException("Exact composition pins are inconsistent with the target corpus or contain validation errors.");
            }

            ValidateUpperHex(composition.OutputRawSha256, 64, "output_raw_sha256");
        }

        private static void ValidateRevocations(string[]? revokedReleaseIds, string currentReleaseId)
        {
            if (revokedReleaseIds == null
                || revokedReleaseIds.Length > SignedUpdateLimits.MaxRevokedReleaseIds
                || revokedReleaseIds.Any(value => !IsSafeId(value))
                || revokedReleaseIds.Distinct(StringComparer.Ordinal).Count() != revokedReleaseIds.Length
                || revokedReleaseIds.Contains(currentReleaseId, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Revoked release id set is invalid or revokes the current release.");
            }
        }

        private static DateTimeOffset ParseUtcSecond(string value, string name)
        {
            if (value.Length != 20
                || !DateTimeOffset.TryParseExact(
                    value,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset result))
            {
                throw new InvalidDataException($"{name} must be canonical RFC3339 UTC with whole-second precision.");
            }

            return result;
        }

        private static Version ParsePatcherVersion(string? value, string name)
        {
            if (value == null || value.Length is < 5 or > 32)
            {
                throw new InvalidDataException($"{name} must be a three-part numeric patcher version.");
            }

            string[] parts = value.Split('.');
            if (parts.Length != 3
                || parts.Any(part => part.Length == 0
                    || part.Length > 9
                    || part.Any(character => !char.IsAsciiDigit(character))
                    || (part.Length > 1 && part[0] == '0'))
                || !Version.TryParse(value, out Version? version))
            {
                throw new InvalidDataException($"{name} must be a three-part numeric patcher version.");
            }

            return version;
        }

        private static byte[] DecodeCanonicalBase64(string? value, int maxDecodedBytes, string name)
        {
            int maxCharacters = checked(((maxDecodedBytes + 2) / 3) * 4);
            if (string.IsNullOrEmpty(value)
                || value.Length > maxCharacters
                || value.Length % 4 != 0
                || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                    || character == '+' || character == '/' || character == '=')))
            {
                throw new InvalidDataException($"{name} is not bounded canonical Base64.");
            }

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(value);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"{name} is not valid Base64.", exception);
            }

            if (decoded.Length > maxDecodedBytes
                || !string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{name} is not canonical Base64 or exceeds its decoded cap.");
            }

            return decoded;
        }

        private static void RejectInvalidUtf8Container(ReadOnlySpan<byte> bytes, int maximumBytes, string name)
        {
            if (bytes.Length is < 2 || bytes.Length > maximumBytes
                || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
            {
                throw new InvalidDataException($"{name} is empty, oversized, or starts with a UTF-8 BOM.");
            }
        }

        private static void ValidateUpperHex(string? value, int length, string name)
        {
            if (value == null || value.Length != length || value.Any(character => !IsUpperHex(character)))
            {
                throw new InvalidDataException($"{name} must be exactly {length} uppercase hexadecimal characters.");
            }
        }

        private static bool FixedEqualsUpperHex(string left, string right)
        {
            byte[] leftBytes = Convert.FromHexString(left);
            byte[] rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static bool IsUpperHex(char character)
        {
            return (character >= '0' && character <= '9') || (character >= 'A' && character <= 'F');
        }

        private static bool IsSafeId(string? value)
        {
            if (value == null || value.Length is < 1 or > 128) return false;
            return char.IsAsciiLetterOrDigit(value[0])
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character == '-' || character == '_' || character == '.');
        }

        private static bool IsSafeVersionToken(string? value)
        {
            return value != null && value.Length is > 0 and <= 64
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character == '-' || character == '_' || character == '.');
        }

        private static bool IsSafeContentVersion(string? value)
        {
            return value != null && value.Length is > 0 and <= 128
                && value.All(character => character is >= (char)0x21 and <= (char)0x7E);
        }

        private static void ValidateDisplayText(string? value, string name, int maximumCharacters)
        {
            if (value == null || value.Length > maximumCharacters)
            {
                throw new InvalidDataException($"{name} is missing or exceeds its character cap.");
            }

            foreach (char character in value)
            {
                bool permittedWhitespace = character == '\r' || character == '\n' || character == '\t';
                bool bidiControl = character == '\u061C'
                    || character == '\u200E' || character == '\u200F'
                    || character is >= '\u202A' and <= '\u202E'
                    || character is >= '\u2066' and <= '\u2069';
                if ((char.IsControl(character) && !permittedWhitespace) || bidiControl)
                {
                    throw new InvalidDataException($"{name} contains a control or bidi-formatting character.");
                }
            }
        }

        private readonly struct ValidationFacts
        {
            public ValidationFacts(
                DateTimeOffset issuedUtc,
                DateTimeOffset expiresUtc,
                bool isExpired,
                SignedUpdatePatcherDisposition patcherDisposition)
            {
                IssuedUtc = issuedUtc;
                ExpiresUtc = expiresUtc;
                IsExpired = isExpired;
                PatcherDisposition = patcherDisposition;
            }

            public DateTimeOffset IssuedUtc { get; }
            public DateTimeOffset ExpiresUtc { get; }
            public bool IsExpired { get; }
            public SignedUpdatePatcherDisposition PatcherDisposition { get; }
        }
    }
}
