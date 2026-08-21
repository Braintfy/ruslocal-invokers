using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace InvokersRu.UpdateReleaseTool
{
    internal static class ReleaseBuilder
    {
        private const string CatalogFileName = "ru_RU.jsonl.br";
        private const string PayloadFileName = "update-manifest.payload.json";
        private const string EnvelopeFileName = "update-envelope.json";
        private const string ReceiptFileName = "update-release-receipt.json";

        public static ReleaseReceipt Build(BuildReleaseRequest request)
        {
            ValidatePaths(request);
            if (request.Sequence == 0 || request.ExpectedPreviousSequence == ulong.MaxValue
                || request.Sequence != request.ExpectedPreviousSequence + 1)
            {
                throw new InvalidOperationException("Sequence must be exactly expected_previous_sequence + 1 and must not be zero.");
            }

            byte[] catalogBytes = StrictIo.ReadRegularFile(
                request.CatalogPath,
                "Translation catalog",
                checked((int)SignedUpdateLimits.MaxUncompressedCatalogBytes));
            StrictIo.DecodeStrictUtf8(catalogBytes, "Translation catalog");
            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(catalogBytes);
            if (catalog.Count < 1 || catalog.Count > SignedUpdateLimits.MaxCatalogRecords)
            {
                throw new InvalidDataException("Translation catalog record count is outside the fixed protocol limits.");
            }

            if (request.TranslationPolicy == "release-approved-v1"
                && catalog.Records.Any(record => !string.Equals(record.Status, "approved", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("release-approved-v1 requires every catalog record to have approved status.");
            }

            SignedUpdateCompatibilityProfile[] compatibility = LoadCompatibility(request.CompatibilityPath);
            string[] revoked = LoadRevocations(request.RevokedReleaseIdsPath);
            string output = StrictIo.FullPath(request.OutputDirectory, "Release output directory");
            string outputParent = Path.GetDirectoryName(output)
                ?? throw new InvalidOperationException("Release output directory has no parent.");
            if (!Directory.Exists(outputParent)) throw new DirectoryNotFoundException("Release output parent must already exist.");
            StrictIo.AssertExistingPathHasNoReparsePoints(outputParent, "Release output parent");
            if (Directory.Exists(output) || File.Exists(output))
            {
                throw new IOException("Release output path already exists. Release directories are immutable and never overwritten.");
            }

            string temporary = Path.Combine(
                outputParent,
                "." + Path.GetFileName(output) + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(temporary);
            bool moved = false;
            try
            {
                string compressedPath = Path.Combine(temporary, CatalogFileName);
                CompressCatalog(catalogBytes, compressedPath);
                byte[] compressedBytes = StrictIo.ReadRegularFile(
                    compressedPath,
                    "Compressed translation catalog",
                    checked((int)SignedUpdateLimits.MaxCompressedCatalogBytes));
                VerifyCompressedCatalog(compressedBytes, catalogBytes);

                using ECDsa signingKey = SigningKeyService.LoadPrivateKey(request.PrivateKeyPath);
                byte[] publicKey = signingKey.ExportSubjectPublicKeyInfo();
                string keyId = SigningKeyService.DeriveKeyId(publicKey);
                var manifest = CreateManifest(
                    request,
                    catalog.Count,
                    catalogBytes,
                    compressedBytes,
                    compatibility,
                    revoked);
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, StrictIo.CompactJson);
                byte[] signature = signingKey.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (signature.Length != 64) throw new CryptographicException("P-256 signing did not produce a 64-byte IEEE-P1363 signature.");
                var envelope = new SignedUpdateEnvelope
                {
                    Schema = 1,
                    Kind = SignedUpdateVerifier.EnvelopeKind,
                    KeyId = keyId,
                    Algorithm = SignedUpdateVerifier.SignatureAlgorithm,
                    PayloadBase64 = Convert.ToBase64String(payload),
                    SignatureBase64 = Convert.ToBase64String(signature)
                };
                byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, StrictIo.CompactJson);
                string payloadHash = StrictIo.Sha256(payload);
                string envelopeHash = StrictIo.Sha256(envelopeBytes);

                SignedUpdateVerifier.Verify(
                    envelopeBytes,
                    publicKey,
                    keyId,
                    new SignedUpdateVerificationContext(DateTimeOffset.UtcNow, request.LatestPatcherVersion));

                StrictIo.WriteNewFile(Path.Combine(temporary, PayloadFileName), payload, "Signed payload output");
                StrictIo.WriteNewFile(Path.Combine(temporary, EnvelopeFileName), envelopeBytes, "Signed envelope output");
                var receipt = new ReleaseReceipt
                {
                    ReleaseId = request.ReleaseId,
                    Sequence = request.Sequence,
                    KeyId = keyId,
                    CatalogFile = CatalogFileName,
                    CatalogRecordCount = catalog.Count,
                    CatalogUncompressedBytes = catalogBytes.LongLength,
                    CatalogUncompressedSha256 = StrictIo.Sha256(catalogBytes),
                    CatalogCompressedBytes = compressedBytes.LongLength,
                    CatalogCompressedSha256 = StrictIo.Sha256(compressedBytes),
                    PayloadFile = PayloadFileName,
                    PayloadSha256 = payloadHash,
                    EnvelopeFile = EnvelopeFileName,
                    EnvelopeSha256 = envelopeHash,
                    CompatibilityProfileCount = compatibility.Length,
                    VerifiedByCore = true
                };
                StrictIo.WriteNewJson(Path.Combine(temporary, ReceiptFileName), receipt, indented: true);

                ReserveSequenceAndPublish(
                    request,
                    keyId,
                    publicKey,
                    envelopeBytes,
                    payloadHash,
                    temporary,
                    output);
                moved = true;
                return receipt;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(catalogBytes);
                if (!moved) StrictIo.TryDeleteDirectory(temporary);
            }
        }

        private static void ValidatePaths(BuildReleaseRequest request)
        {
            string repositoryRoot = StrictIo.FullPath(request.RepositoryRoot, "Repository root");
            StrictIo.AssertExistingPathHasNoReparsePoints(repositoryRoot, "Repository root");
            StrictIo.AssertOutsideRepository(request.PrivateKeyPath, repositoryRoot, "Private signing key");
            StrictIo.AssertOutsideRepository(request.SigningStatePath, repositoryRoot, "Signing state");
            StrictIo.AssertExistingPathHasNoReparsePoints(request.PrivateKeyPath, "Private signing key");
            StrictIo.AssertExistingPathHasNoReparsePoints(request.SigningStatePath, "Signing state");
            string keyParent = Path.GetDirectoryName(StrictIo.FullPath(request.PrivateKeyPath, "Private signing key")) ?? string.Empty;
            string stateParent = Path.GetDirectoryName(StrictIo.FullPath(request.SigningStatePath, "Signing state")) ?? string.Empty;
            if (!string.Equals(keyParent, stateParent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Private key and signing state must be kept in the same protected directory.");
            }
        }

        private static SignedUpdateCompatibilityProfile[] LoadCompatibility(string path)
        {
            byte[] bytes = StrictIo.ReadRegularFile(path, "Compatibility profile array", SignedUpdateLimits.MaxPayloadBytes);
            SignedUpdateCompatibilityProfile[] profiles = StrictIo.DeserializeStrict<SignedUpdateCompatibilityProfile[]>(bytes, "Compatibility profile array");
            if (profiles.Length is < 1 or > SignedUpdateLimits.MaxCompatibilityProfiles)
            {
                throw new InvalidDataException("Compatibility profile array is empty or exceeds the fixed protocol limit.");
            }

            return profiles;
        }

        private static string[] LoadRevocations(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            byte[] bytes = StrictIo.ReadRegularFile(path, "Revoked release id array", 128 * 1024);
            return StrictIo.DeserializeStrict<string[]>(bytes, "Revoked release id array");
        }

        private static void CompressCatalog(byte[] source, string destination)
        {
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            using (var brotli = new BrotliStream(file, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                brotli.Write(source, 0, source.Length);
            }

            file.Flush(flushToDisk: true);
        }

        private static void VerifyCompressedCatalog(byte[] compressed, byte[] expected)
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
            using var output = new MemoryStream(expected.Length);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int read = brotli.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > SignedUpdateLimits.MaxUncompressedCatalogBytes)
                {
                    throw new InvalidDataException("Brotli self-check exceeded the catalog expansion limit.");
                }

                output.Write(buffer, 0, read);
            }

            byte[] expanded = output.ToArray();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(expanded), SHA256.HashData(expected))
                || expanded.LongLength != expected.LongLength)
            {
                throw new InvalidDataException("Brotli round-trip did not reproduce the exact catalog bytes.");
            }
        }

        private static SignedUpdateManifest CreateManifest(
            BuildReleaseRequest request,
            int recordCount,
            byte[] uncompressed,
            byte[] compressed,
            SignedUpdateCompatibilityProfile[] compatibility,
            string[] revoked)
        {
            return new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = request.Sequence,
                ReleaseId = request.ReleaseId,
                IssuedUtc = request.IssuedUtc,
                ExpiresUtc = request.ExpiresUtc,
                Patcher = new SignedUpdatePatcher
                {
                    MinimumVersion = request.MinimumPatcherVersion,
                    LatestVersion = request.LatestPatcherVersion,
                    DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
                },
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = request.ArtifactId,
                    Url = "https://github.com/Braintfy/ruslocal-invokers/releases/download/"
                        + request.ReleaseId + "/" + CatalogFileName,
                    Compression = "brotli",
                    CompressedBytes = compressed.LongLength,
                    CompressedSha256 = StrictIo.Sha256(compressed),
                    UncompressedBytes = uncompressed.LongLength,
                    UncompressedSha256 = StrictIo.Sha256(uncompressed),
                    RecordCount = recordCount,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = request.TranslationPolicy
                },
                Compatibility = compatibility,
                RevokedReleaseIds = revoked,
                NotesRu = request.NotesRu
            };
        }

        private static void ReserveSequenceAndPublish(
            BuildReleaseRequest request,
            string keyId,
            byte[] publicKey,
            byte[] envelopeBytes,
            string payloadHash,
            string temporaryOutput,
            string finalOutput)
        {
            string statePath = StrictIo.FullPath(request.SigningStatePath, "Signing state");
            string lockPath = statePath + ".lock";
            StrictIo.AssertExistingPathHasNoReparsePoints(lockPath, "Signing state lock");
            using var stateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            SigningSequenceState state = LoadAndValidateState(statePath, keyId);
            if (state.HighestReservedSequence != request.ExpectedPreviousSequence)
            {
                throw new InvalidOperationException(
                    $"Signing state is at sequence {state.HighestReservedSequence}, not expected sequence {request.ExpectedPreviousSequence}. Refresh inputs; never reuse a sequence.");
            }

            if (state.Records.Any(record => record.Sequence == request.Sequence
                || string.Equals(record.ReleaseId, request.ReleaseId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Sequence or release_id has already been reserved. Signed release identities are append-only.");
            }

            string? previousHash = state.Records
                .Where(record => record.Sequence == state.HighestReservedSequence)
                .Select(record => record.PayloadSha256)
                .SingleOrDefault();
            SignedUpdateVerifier.Verify(
                envelopeBytes,
                publicKey,
                keyId,
                new SignedUpdateVerificationContext(
                    DateTimeOffset.UtcNow,
                    request.LatestPatcherVersion,
                    state.HighestReservedSequence,
                    state.HighestReservedSequence == 0 ? null : previousHash));

            var record = new SigningSequenceRecord
            {
                Sequence = request.Sequence,
                ReleaseId = request.ReleaseId,
                PayloadSha256 = payloadHash,
                ReservedUtc = StrictIo.CanonicalUtcSecond(DateTimeOffset.UtcNow),
                Status = "reserved"
            };
            state.Records = state.Records.Concat(new[] { record }).ToArray();
            state.HighestReservedSequence = request.Sequence;
            StrictIo.WriteJsonAtomically(statePath, state);
            StrictIo.ProtectSecretFile(statePath);

            Directory.Move(temporaryOutput, finalOutput);
            record.Status = "completed";
            record.CompletedUtc = StrictIo.CanonicalUtcSecond(DateTimeOffset.UtcNow);
            StrictIo.WriteJsonAtomically(statePath, state);
            StrictIo.ProtectSecretFile(statePath);
        }

        private static SigningSequenceState LoadAndValidateState(string path, string expectedKeyId)
        {
            byte[] bytes = StrictIo.ReadRegularFile(path, "Signing state", 1024 * 1024);
            SigningSequenceState state = StrictIo.DeserializeStrict<SigningSequenceState>(bytes, "Signing state");
            if (state.Schema != 1
                || state.Kind != "invokers-ru-update-signing-state"
                || !string.Equals(state.KeyId, expectedKeyId, StringComparison.Ordinal)
                || state.Records.Length > 100_000)
            {
                throw new InvalidDataException("Signing state identity, key id, or record count is invalid.");
            }

            var sequences = new HashSet<ulong>();
            var releaseIds = new HashSet<string>(StringComparer.Ordinal);
            ulong maximum = 0;
            for (int index = 0; index < state.Records.Length; index++)
            {
                SigningSequenceRecord record = state.Records[index];
                if (record.Sequence == 0
                    || record.Sequence != checked((ulong)index + 1)
                    || !sequences.Add(record.Sequence)
                    || !releaseIds.Add(record.ReleaseId)
                    || !StrictIo.IsUpperSha256(record.PayloadSha256)
                    || (record.Status != "reserved" && record.Status != "completed")
                    || !IsCanonicalUtcSecond(record.ReservedUtc)
                    || (record.Status == "completed") != (record.CompletedUtc != null)
                    || (record.CompletedUtc != null && !IsCanonicalUtcSecond(record.CompletedUtc)))
                {
                    throw new InvalidDataException("Signing state contains a reused or malformed release record.");
                }

                maximum = Math.Max(maximum, record.Sequence);
            }

            if (maximum != state.HighestReservedSequence
                || (state.HighestReservedSequence == 0) != (state.Records.Length == 0)
                || state.HighestReservedSequence != checked((ulong)state.Records.Length)
                || sequences.Any(sequence => sequence > state.HighestReservedSequence)
                || (state.HighestReservedSequence > 0
                    && Enumerable.Range(1, state.Records.Length).Any(value => !sequences.Contains(checked((ulong)value)))))
            {
                throw new InvalidDataException("Signing state is not a contiguous append-only sequence ledger.");
            }

            return state;
        }

        private static bool IsCanonicalUtcSecond(string? value)
        {
            return value != null
                && value.Length == 20
                && DateTimeOffset.TryParseExact(
                    value,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _);
        }
    }
}
